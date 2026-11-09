/* fire native runtime - the core that the code `fire.Native` generates from the bytecode is built on.
 *
 * Design rules (see docs/spec/NATIVE_BACKEND.md):
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
#include <stdarg.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef FIRE_LIBRARY
#include "fire_pkg_abi.h"   /* (the library for the VM: the host structure) */
#endif
/* The dialect of the C compiler (attributes, thread-local storage, checks when compiling): one small file per compiler family, cc/fire_cc_*.h. */
#include "cc/fire_cc.h"

/* The platform layer (docs/spec/NATIVE_BACKEND.md, "Plattformschicht"): everything that depends on the operating system or the board - exit, clock, and for fire
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

FIRE_CC_RUNTIME_BEGIN
#define FIRE_FN static inline

/* Thread-local storage of the compiler (only with fire threads; FIRE_TLS_STRUCT platforms keep the state on the task instead). */
#if defined(FIRE_THREADS) && !defined(FIRE_TLS_STRUCT)
#define FIRE_TLS FIRE_TLS_KEYWORD
#else
#define FIRE_TLS
#endif

/* Linkage "shared" on Windows (FIRE_DLL, docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung"): the runtime is a DLL of its own (FIRE_DLL_RUNTIME: the translation unit that
 * is it), the program and the DLLs of the library modules import its state (FIRE_RT_DATA) and functions (FIRE_RT_FN). A DLL cannot import from the program: what the
 * runtime or a module calls of the program's code (the hooks: makeClassError, the JSON tables, ...) and the program's data (g_dimNames, the tables of the modules) are
 * pointers in the DLL (FIRE_DLL_LINKED: the generated head names them `(*fire_hp_name)`), filled by its fire_link_* function from the program's table when it starts. */
#if defined(FIRE_DLL_RUNTIME)
#define FIRE_RT_DATA __declspec(dllexport)
#define FIRE_RT_FN __declspec(dllexport)
#elif defined(FIRE_DLL)
#define FIRE_RT_DATA __declspec(dllimport)
#define FIRE_RT_FN
#else
#define FIRE_RT_DATA
#define FIRE_RT_FN
#endif
#if defined(FIRE_DLL_LINKED) && !defined(FIRE_DLL_LINKED_ELSEWHERE)
#define FIRE_HOST_EXTERN   /* (a pointer of this DLL, see above) */
#else   /* (FIRE_DLL_LINKED_ELSEWHERE: the natives in C++ of a fire plugin - the pointers are those of its C file) */
#define FIRE_HOST_EXTERN extern
#endif
#ifdef __cplusplus
#define FIRE_BEGIN extern "C" { namespace fire {
#define FIRE_END } }
#define FIRE_STATIC_ASSERT(c, m) static_assert(c, m)
#else
#define FIRE_BEGIN
#define FIRE_END
#define FIRE_STATIC_ASSERT(c, m) FIRE_STATIC_ASSERT_C(c, m)
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

#ifdef FIRE_DEBUG
FIRE_RT_FN void fire_dbgExited(int code);   /* the debugger hears that the program ends (fire_debug.h) */
#endif
/* Ends the process. With threads the other threads must not run static destructors under their feet: no cleanup, only the streams are flushed. */
FIRE_NORETURN FIRE_FN void exitNow(int code) {
#ifdef FIRE_DEBUG
    fire_dbgExited(code);
#endif
    fflush(stdout);
    fflush(stderr);
    fire_plat_exit(code);
}

/* The kinds of a value. A pointer has one kind per target (see "`ref` parameters"): a variable or a field, an element of a byte buffer, an element of an
 * array. They follow one another, so that `isPtr` is a single comparison. */
enum { K_Bool, K_Int, K_Float, K_Char, K_String, K_Class, K_Lambda, K_PtrVar, K_PtrByte, K_PtrElem, K_Array, K_Buffer, K_Undefined, K_Word, K_PtrField };
/* (K_PtrField: a pointer to a field declared `defined` - a packed C number or a Value slot, written like an assignment to the field: fieldPtrV) */
#define FIRE_IS_PTR_KIND(k) ((unsigned)(k) - (unsigned)K_PtrVar <= (unsigned)(K_PtrElem - K_PtrVar) || (k) == K_PtrField)

/* The width of `int`: 64 bits, 32 when the program is generated with `#intwidth 32` (FIRE_INT32; SPEC 8.2.1), 16 with `#intwidth 16` (FIRE_INT16: the
 * storage stays 32 bits - the field also holds chars up to 10FFFFh - and every int that is made is cut to 16, FIRE_INT_CUT). An operation computes in 64 bits
 * (the operands are widened) and the result is cut when it is stored - like the VM, which cuts every int it makes. */
#ifdef FIRE_INT16
#ifndef FIRE_INT32
#define FIRE_INT32 1
#endif
#define FIRE_INT_CUT(v) ((int64_t)(int16_t)(v))
#elif defined(FIRE_INT32)
#define FIRE_INT_CUT(v) ((int64_t)(int32_t)(v))
#else
#define FIRE_INT_CUT(v) ((int64_t)(v))
#endif
#ifdef FIRE_INT32
typedef int32_t FireInt;
typedef uint32_t FireUInt;   /* (the arithmetic of the generator's scalar ints: it wraps like FIRE_INT_CUT) */
#define FIRE_FIREINT_BITS 32
#define FIRE_INT_UNDEF INT32_MIN   /* (an `undefined` element of a packed int array; a real INT32_MIN is marked in `real`) */
#else
typedef int64_t FireInt;
typedef uint64_t FireUInt;
#define FIRE_INT_UNDEF INT64_MIN
#define FIRE_FIREINT_BITS 64
#endif
/* `<<` and `>>` of the generator's scalar ints, as shl/shr compute them (in 64 bits, the count & 63, then cut to the program's int) - a C shift by the
 * whole width or more is undefined */
#define FIRE_SCALAR_SHL(a, s) ((int)((s) & 63) >= FIRE_FIREINT_BITS ? (FireInt)0 : (FireInt)((FireUInt)(a) << ((s) & 63)))
#define FIRE_SCALAR_SHR(a, s) ((int)((s) & 63) >= FIRE_FIREINT_BITS ? (FireInt)((a) < 0 ? -1 : 0) : (FireInt)((a) >> ((s) & 63)))

/* `word` (SPEC 8.10): the machine word of the target, unsigned, without a unit - K_Word, its bits in `w` (zero above the width). The generator defines
 * FIRE_WORD_BITS from the target (`wordWidth`); without it (a package library for the VM) the width of a pointer. An operation on words wraps at the
 * width (wordOp). */
#ifndef FIRE_WORD_BITS
#if UINTPTR_MAX > 0xFFFFFFFFu
#define FIRE_WORD_BITS 64
#else
#define FIRE_WORD_BITS 32
#endif
#endif
#if FIRE_WORD_BITS == 16
typedef uint16_t FireWord;
#define FIRE_WORD_MASK 0xFFFFull
#elif FIRE_WORD_BITS == 32
typedef uint32_t FireWord;
#define FIRE_WORD_MASK 0xFFFFFFFFull
#elif FIRE_WORD_BITS == 64
typedef uint64_t FireWord;
#define FIRE_WORD_MASK 0xFFFFFFFFFFFFFFFFull
#else
#error "FIRE_WORD_BITS must be 16, 32 or 64"
#endif

/* A runtime value, trivially copyable, 16 bytes on every target: it holds whatever a variable of type `any` can hold - an int of 64 bits too (docs/spec/TYPES.md:
 * the type of a variable is fixed, a value that is statically narrow is stored narrow where its declaration says so; the value itself is always the wide one).
 * `kind` says what it is; `unit` indexes the program's unit table (0 = unitless) - for an object, array or buffer it is the slot of its handle and `reserved`
 * the generation (for an enum value `reserved` is its enum). `wide`: an int of an explicit width beyond the program's `int` (SPEC 8.2.1: `int[64]` with
 * `#intwidth 32`) - 64 or 32, 0 for an int of the program's width (Value._wide of the VM); an operation with such an operand computes in its width (IntOp). */
typedef struct Value {
    uint8_t kind;
    uint8_t wide;       /* K_Int: 0, 32 or 64 (see above); 0 for everything else */
    uint16_t reserved;
    uint32_t unit;
    union {
        int64_t i;      /* K_Int (cut to its width: FIRE_INT_CUT, IntW), K_Bool, K_Char */
        Real f;
        const void* p;
        uint64_t w;     /* K_Word */
    };
} Value;
FIRE_STATIC_ASSERT(sizeof(Value) == 16, "Value must stay 16 bytes");
#define FIRE_PAD0(r) (r).wide = 0;
typedef uint16_t FireGen;
#define FIRE_GEN_MASK 0xFFFFu
#define FIRE_UNIT_MAX 0xFFFFFFFFu
#define FIRE_SLOT_MAX 0xFFFFFFFEu
#define FIRE_UNIT_OFFSET(v) ((int32_t)(v).unit)
#define FIRE_VGEN(v) ((FireGen)(v).reserved)
#define FIRE_SETGEN(r, g) ((r).reserved = (FireGen)(g))
FIRE_FN bool isPtr(Value v) { return FIRE_IS_PTR_KIND(v.kind); }

/* The size of a header that values follow (arrays, objects, lambdas): rounded up so that the values behind it are aligned. */
#define FIRE_HDR(T) ((sizeof(T) + 7u) & ~(size_t)7u)

/* ---------------------------------------------------------------------------------------------------------------------
 * Construction
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN Value fire_IntU(int64_t v, uint32_t unit) { Value r; r.kind = K_Int; FIRE_PAD0(r) r.reserved = 0; r.unit = unit; r.i = FIRE_INT_CUT(v); return r; }
/* An int that keeps the width `wide` (64, 32; 0: the program's) - Value.CutInt of the VM. Without `#intwidth` every int has 64 bits and the mark says nothing. */
#ifdef FIRE_INT32
FIRE_FN Value IntW(int64_t v, uint32_t unit, uint8_t wide) {
    Value r; r.kind = K_Int; r.wide = wide; r.reserved = 0; r.unit = unit;
    r.i = wide >= 64 ? v : wide == 32 ? (int64_t)(int32_t)v : FIRE_INT_CUT(v);
    return r;
}
/* The result of an operation on the ints `a` and `b`: in the wider of the widths they keep (Value.W). */
#define FIRE_W2(a, b) ((a).wide >= (b).wide ? (a).wide : (b).wide)
#define IntOp(v, unit, a, b) IntW((v), (unit), FIRE_W2(a, b))
#define IntOp1(v, unit, a) IntW((v), (unit), (a).wide)
/* A whole number from native code (a package; its ints have 64 bits): one that does not fit the program's int keeps its 64 bits (Value.MakeNativeInt) -
 * the native meant it (the ticks of a clock, the size of a file). */
FIRE_FN Value IntNativeU(int64_t v, uint32_t unit) { return IntW(v, unit, FIRE_INT_CUT(v) == v ? 0 : 64); }
#else
FIRE_FN Value IntW(int64_t v, uint32_t unit, uint8_t wide) { Value r = fire_IntU(v, unit); (void)wide; return r; }
#define IntOp(v, unit, a, b) fire_IntU((v), (unit))
#define IntOp1(v, unit, a) fire_IntU((v), (unit))
#define IntNativeU(v, unit) fire_IntU((v), (unit))
#endif
#define IntNative(v) IntNativeU((v), 0)
FIRE_FN Value fire_Int1(int64_t v) { return fire_IntU(v, 0); }
FIRE_FN Value fire_FloatU(Real v, uint32_t unit) { Value r; r.kind = K_Float; FIRE_PAD0(r) r.reserved = 0; r.unit = unit; r.f = v; return r; }
FIRE_FN Value fire_Float1(Real v) { return fire_FloatU(v, 0); }
FIRE_FN Value Bool(bool v) { Value r; r.kind = K_Bool; FIRE_PAD0(r) r.reserved = 0; r.unit = 0; r.i = v ? 1 : 0; return r; }
FIRE_FN Value Char(uint32_t c) { Value r; r.kind = K_Char; FIRE_PAD0(r) r.reserved = 0; r.unit = 0; r.i = c; return r; }
FIRE_FN Value UndefU(uint32_t unit) { Value r; r.kind = K_Undefined; FIRE_PAD0(r) r.reserved = 0; r.unit = unit; r.i = 0; return r; }
FIRE_FN Value Undef(void) { return UndefU(0); }
/* A machine word (SPEC 8.10): the bits cut to FIRE_WORD_BITS. */
FIRE_FN Value WordV(uint64_t v) { Value r; r.kind = K_Word; FIRE_PAD0(r) r.reserved = 0; r.unit = 0; r.i = 0; r.w = (FireWord)(v & FIRE_WORD_MASK); return r; }

/* ---- Enums (docs/language/10-enums-and-namespaces.md): a value of an enum is an Int that carries the number of its enum (1-based) in `reserved`; the generated
 * program sets the table (fire_init). Every operation on whole numbers works on it as before (and gives a plain int: Int() clears `reserved`). */
typedef struct FireEnumMember { const char* name; int64_t value; } FireEnumMember;
typedef struct FireEnum { const char* name; uint8_t flags; const FireEnumMember* members; uint32_t count; } FireEnum;
extern FIRE_RT_DATA const FireEnum* g_enums;
extern FIRE_RT_DATA uint32_t g_enumCount;
/* A value of the enum `index` (1-based, the generated table). */
FIRE_FN Value EnumV(int64_t v, uint16_t index) { Value r; r.kind = K_Int; FIRE_PAD0(r) r.reserved = index; r.unit = 0; r.i = FIRE_INT_CUT(v); return r; }
/* The enum of a value (null: none). */
FIRE_FN const FireEnum* enumOf(Value v) { return v.kind == K_Int && v.reserved != 0 && v.reserved <= g_enumCount ? &g_enums[v.reserved - 1] : NULL; }
/* `undefined` as a constant initializer (a global or static of the generated program: C has no dynamic initialization) */
#define FIRE_UNDEF_INIT {K_Undefined, 0, 0, 0, {0}}
/* `Int(v)`, `Int(v, unit)`: an int of the program, cut to its width. Around the C of a package the generated file switches FIRE_INT_U/FIRE_INT_1 to the
 * native ints (IntNative: one that does not fit keeps its 64 bits - what the VM does with every int a native of a package returns, Value.MakeNativeInt). */
#define FIRE_INT_U fire_IntU
#define FIRE_INT_1 fire_Int1
#define Int(...) FIRE_EXPAND(FIRE_PICK2(__VA_ARGS__, FIRE_INT_U, FIRE_INT_1, 0)(__VA_ARGS__))
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
/* Property accessors (SPEC 2.2 "Ownership in property accessors"): what the `this` of the running getters took over (a `return` of it goes to the caller after
 * all), and the argument list in which a property assignment of an accessor hands a value to the setter it calls (the setter takes it on entry). */
typedef struct AccEntry { Owned* o; uint32_t slot; FireGen gen; } AccEntry;
typedef struct AccState { AccEntry* items; uint32_t top, cap; OwnList* handOff; } AccState;
#ifdef __cplusplus
#define FIRE_ZERO_INIT {}
#else
#define FIRE_ZERO_INIT {0}
#endif
/* The debugger (fire_debug.h): the top of the shadow stack of this thread, the depth of the code the VM would run nested, the thread for the debugger. */
typedef struct DbgFrame DbgFrame;
typedef struct DbgThread DbgThread;
#ifdef FIRE_DEBUG
extern FIRE_RT_DATA volatile int g_dbgArmed;     /* a debugger is attached */
#define FIRE_DEBUG_THREAD_VARS(X) \
    X(DbgFrame*, g_dbgTop, NULL) \
    X(int32_t, g_dbgNest, 0) \
    X(DbgThread*, g_dbgSelf, NULL)
#else
#define FIRE_DEBUG_THREAD_VARS(X)
#endif
#define FIRE_THREAD_VARS(X) \
    FIRE_DEBUG_THREAD_VARS(X) \
    X(UnwindState, g_unwind, FIRE_ZERO_INIT) \
    X(Pool, g_pool, FIRE_ZERO_INIT) \
    X(Handler*, g_handlers, NULL) \
    X(OwnList*, g_globalOwn, NULL)   /* the global scope: thrown exceptions belong to it (SPEC 7.6) */ \
    X(Pending*, g_pending, NULL) \
    X(uint64_t, g_throwToken, 0) \
    X(uint8_t, g_isThread, 0)           /* 1 on a fire thread */ \
    X(int32_t, g_jobDepth, 0)           /* a `fire global` job is running on this thread */ \
    X(int32_t, g_noSyncDepth, 0)        /* something runs for itself (a destructor, a host service, `catch threads`/`catch terminate`): no automatic queue in it */ \
    X(uint32_t, g_attnMask, 7u)         /* the signals this thread looks at (ATTN_ALL|ATTN_MAIN|ATTN_SECT); fire threads clear all but ATTN_ALL */ \
    X(int32_t, g_tick, 4096) \
    X(uint8_t, g_leaving, 0)            /* unwinding because of leave/terminate (or an unhandled exception): no more signals */ \
    X(uint8_t, g_threadFailed, 0)       /* the thread ends with an unhandled exception: its global scope stays */ \
    X(OwnList*, g_travel, NULL) \
    X(SectionReq*, g_curSection, NULL) \
    X(int32_t, g_secDepth, 0) \
    X(OwnList* const*, g_scopeLists, NULL)   /* a call of an ownership method with a `Takes` mode through the dispatcher: the scopes of the call (see ownMethodT) */ \
    X(int32_t, g_scopeCount, 0) \
    X(OwnList*, g_argIn, NULL)          /* `try x.Take...`: the argument list of the call that is starting - set right before the call, taken by the callee on entry (MYARGS) */ \
    X(uint32_t, g_reflCaller, 0xFFFFFFFFu)   /* the class of the code that called into the reflection library */ \
    X(const void*, g_callTArgs, NULL)       /* the type arguments for the next call of a generic method (TArgs) */ \
    X(int32_t, g_explicitTArgs, 0)           /* the call names its type arguments (`M<string>(x)`): their number - only generic overloads with that many count */ \
    X(Zombies, g_zombies, FIRE_ZERO_INIT)          /* destroyed objects whose memory is freed at the end of the batch */ \
    X(BridgeError, g_ioError, FIRE_ZERO_INIT)      /* the IO bridge: the last error of this thread */ \
    X(BridgeError, g_deviceError, FIRE_ZERO_INIT)  /* the device bridge */ \
    X(BridgeError, g_gfxError, FIRE_ZERO_INIT)     /* the graphics bridge: the message of the last failed image function */ \
    X(AccState, g_acc, FIRE_ZERO_INIT)             /* property accessors: what their `this` took over, the hand-over to a setter */

#if defined(FIRE_THREADS) && (defined(FIRE_TLS_STRUCT) || defined(FIRE_DLL))
typedef struct ThreadLocals {
#define FIRE_X(type, name, init) type name;
    FIRE_THREAD_VARS(FIRE_X)
#undef FIRE_X
} ThreadLocals;
#ifdef FIRE_DLL
/* Linkage "shared" on Windows: a DLL cannot hand out its thread-local variables (__declspec(thread) cannot be imported) - the runtime DLL keeps them in one block per
 * thread, the program and the modules ask it for the block of the calling thread (the same block for the whole life of a thread: FIRE_CONST_FN lets the compiler
 * ask once per function). */
#ifdef FIRE_DLL_RUNTIME
#define FIRE_XI(type, name, init) init,
static FIRE_TLS ThreadLocals fire_tlsBlock = { FIRE_THREAD_VARS(FIRE_XI) };
#undef FIRE_XI
#define FIRE_TLV(name) (fire_tlsBlock.name)
#else
#define FIRE_TLV(name) (fire_tl()->name)
#endif
FIRE_RT_FN ThreadLocals* fire_tl(void) FIRE_CONST_FN;
#else
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
#define FIRE_TLV(name) (fire_tl()->name)
#endif
#define g_unwind FIRE_TLV(g_unwind)
#define g_pool FIRE_TLV(g_pool)
#define g_handlers FIRE_TLV(g_handlers)
#define g_globalOwn FIRE_TLV(g_globalOwn)
#define g_pending FIRE_TLV(g_pending)
#define g_throwToken FIRE_TLV(g_throwToken)
#define g_isThread FIRE_TLV(g_isThread)
#define g_jobDepth FIRE_TLV(g_jobDepth)
#define g_noSyncDepth FIRE_TLV(g_noSyncDepth)
#define g_attnMask FIRE_TLV(g_attnMask)
#define g_tick FIRE_TLV(g_tick)
#define g_leaving FIRE_TLV(g_leaving)
#define g_threadFailed FIRE_TLV(g_threadFailed)
#define g_travel FIRE_TLV(g_travel)
#define g_curSection FIRE_TLV(g_curSection)
#define g_secDepth FIRE_TLV(g_secDepth)
#define g_argIn FIRE_TLV(g_argIn)
#define g_scopeLists FIRE_TLV(g_scopeLists)
#define g_scopeCount FIRE_TLV(g_scopeCount)
#define g_reflCaller FIRE_TLV(g_reflCaller)
#define g_callTArgs FIRE_TLV(g_callTArgs)
#define g_explicitTArgs FIRE_TLV(g_explicitTArgs)
#define g_zombies FIRE_TLV(g_zombies)
#define g_ioError FIRE_TLV(g_ioError)
#define g_deviceError FIRE_TLV(g_deviceError)
#define g_gfxError FIRE_TLV(g_gfxError)
#define g_acc FIRE_TLV(g_acc)
#ifdef FIRE_DEBUG
#define g_dbgTop FIRE_TLV(g_dbgTop)
#define g_dbgNest FIRE_TLV(g_dbgNest)
#define g_dbgSelf FIRE_TLV(g_dbgSelf)
#endif
#else
#define FIRE_X(type, name, init) extern FIRE_RT_DATA FIRE_TLS type FIRE_CC_NEAR_DATA name;   /* (FIRE_CC_NEAR_DATA: the 16-bit real mode keeps them in the data segment of the program - a test of g_unwind is one instruction) */
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
/* (in a package library an exception of the language goes to the VM, which throws it there) */
FIRE_NORETURN FIRE_FN void fatalException(const char* cls, const char* message) { fireError(cls, message); }
#elif defined(FIRE_LIBRARY)
/* The natives of a package in C built as a shared library for the virtual machine (native/abi/fire_pkg_wrapper.h): a run-time error does not end the process - it
 * jumps back (longjmp) to the wrapper of the entry point, which reports it to the VM. (So a native that calls fireError or fatal must not hold anything it has to
 * release first: memory it allocated with malloc, a lock.) The calls into the library are serialised: one jump target is enough. */
#include <setjmp.h>
typedef struct FireLibraryError { int kind; char cls[96]; char text[256]; } FireLibraryError;   /* kind 1: an error of the runtime, 2: an exception of the program */
extern jmp_buf* g_libraryJump;
extern FireLibraryError g_libraryError;
/* What the host of the VM decides (fire_pkg_abi.h: `fire_host`); null until the host has told the library. */
extern const fire_host* g_libraryHost;
FIRE_FN const fire_host* libraryHost(void) { return g_libraryHost; }
FIRE_NORETURN FIRE_FN void fireLibraryJump(int kind, const char* cls, const char* text) {
    g_libraryError.kind = kind;
    snprintf(g_libraryError.cls, sizeof g_libraryError.cls, "%s", cls);
    snprintf(g_libraryError.text, sizeof g_libraryError.text, "%s", text);
    if (g_libraryJump == NULL) { fprintf(stderr, "fire package library: %s\n", text); abort(); }
    longjmp(*g_libraryJump, 1);
}
FIRE_NORETURN FIRE_FN void fatal(const char* message) { fireLibraryJump(1, "", message); }
FIRE_NORETURN FIRE_FN void unsupported(const char* what) {
    char text[256];
    snprintf(text, sizeof text, "'%s' is not supported in a package library", what);
    fireLibraryJump(1, "", text);
}
FIRE_NORETURN FIRE_FN Value fireError(const char* cls, const char* utf8) { fireLibraryJump(2, cls, utf8); }
/* (in a package library an exception of the language goes to the VM, which throws it there) */
FIRE_NORETURN FIRE_FN void fatalException(const char* cls, const char* message) { fireLibraryJump(2, cls, message); }
#ifndef FIRE_THREADS
/* The services of the runtime for a package with a thread of its own (the windows' managers), in a library for the VM: what the main program of a native build does
 * at its safe points, the VM does - the library asks it (fire_host.request_service), and it calls fire_pkg_service of the library back on its thread. The holds on the
 * end of the program are the VM's (fire_host.hold). The GIL of a native build is the lock of the library's calls (fire_pkg_wrapper.h): a native that waits lets go of it. */
typedef void (*HostService)(void);
extern HostService g_hostServices[8];
extern uint32_t g_hostServiceCount;
extern fire_atomic32 g_hostPending;
#define FIRE_HOST_HAS(field) (g_libraryHost != NULL && (size_t)g_libraryHost->size >= offsetof(fire_host, field) + sizeof g_libraryHost->field && g_libraryHost->field != NULL)
FIRE_FN void hostAddService(HostService service) { if (g_hostServiceCount < 8) g_hostServices[g_hostServiceCount++] = service; }
FIRE_FN void hostRequest(void) {
    fire_atomic_store(&g_hostPending, 1);
    if (FIRE_HOST_HAS(request_service)) g_libraryHost->request_service();
}
FIRE_FN void hostHold(int32_t delta) { if (FIRE_HOST_HAS(hold)) g_libraryHost->hold(delta); }
/* (fire_pkg_service: the library's services, if one was asked for) */
FIRE_FN void hostServe(void) {
    uint32_t i;
    if (fire_atomic_exchange(&g_hostPending, 0) == 0) return;
    for (i = 0; i < g_hostServiceCount; i++) g_hostServices[i]();
}
void fire_pkgLock(void);
void fire_pkgUnlock(void);
FIRE_FN void gilAcquire(void) { fire_pkgLock(); }
FIRE_FN void gilRelease(void) { fire_pkgUnlock(); }
#endif
#else
FIRE_NORETURN FIRE_COLD FIRE_FN void fatal(const char* message) {
    fflush(stdout);
    fprintf(stderr, "fire runtime error: %s\n", message);
    exitNow(1);
}

/* An exception of the language that nothing catches (a program without exceptions ends at the first one): the text and the exit code of the VM's command line
 * runner (UncaughtScriptException: "Unhandled exception of type 'Class': message", exit code 1). */
FIRE_NORETURN FIRE_COLD FIRE_FN void fatalException(const char* cls, const char* message) {
    fflush(stdout);
    fprintf(stderr, "Unhandled exception of type '%s': %s\n", cls, message);
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
/* The int of a Value for a typed native in C (docs/packages/README.md "Typed functions"): lenient like the natives read their Values - a float gives its whole part. */
FIRE_FN int64_t fire_natI(Value v) { return v.kind == K_Float ? (int64_t)v.f : (int64_t)v.i; }
/* (the same without a copy of the Value, for the generated code: `v` is a variable) */
#define FIRE_NATI(v) ((v).kind == K_Float ? (int64_t)(v).f : (int64_t)(v).i)
#define FIRE_NATR(v) ((v).kind == K_Float ? (v).f : (Real)(v).i)

/* The fewest digits (`%.*e`) that read back as the same number: `digits` is d.ddde+XX. `single`: the number is a float32. */
FIRE_FN void fire_shortestE(double v, bool single, char* sci, size_t cap) {
    int lo = 0, hi = single ? 8 : 16;
    /* (more digits never read back worse: the smallest precision that works is found by bisection) */
    while (lo < hi) {
        int mid = (lo + hi) / 2;
        snprintf(sci, cap, "%.*e", mid, v);
        /* (stored through volatile: a compiler that keeps the x87 registers - Open Watcom - would compare with more bits than a float or a double has) */
        volatile double back = strtod(sci, NULL);
        volatile float backF = (float)back, vF = (float)v;
        if (single ? backF == vF : back == v) hi = mid; else lo = mid + 1;
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
/* The base symbols, in the order of the dimension vectors (sorted like the VM prints them). Defined by the generated code; with linkage "shared" a fire plugin
 * puts the dimensions of its units the program does not have into its free (empty) places - then g_dimsAdded is set and the names are printed in their order
 * (unitDimOrder), not in that of the places. */
FIRE_HOST_EXTERN const char* g_dimNames[];

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
extern FIRE_RT_DATA const UnitConvFn* g_unitTo;
extern FIRE_RT_DATA const UnitConvFn* g_unitFrom;
extern FIRE_RT_DATA UnitDef* g_ud;
extern FIRE_RT_DATA uint32_t g_udCount, g_udCap;
extern FIRE_RT_DATA bool g_dimsAdded;

FIRE_FN bool unitDimsZero(const int32_t* dims) {
    int i;
    for (i = 0; i < FIRE_NDIMS; i++) if (dims[i]) return false;
    return true;
}
/* Unit.IsUnitless: no dimensions (whatever the scale). */
FIRE_FN bool unitIsUnitless(uint32_t u) { return unitDimsZero(g_ud[u].dims); }
/* The places of the dimensions in the order of their names, as the VM sorts them (letters without case first, then lower before upper case): the order of the
 * places, unless a plugin added dimensions (g_dimsAdded). */
FIRE_FN int unitDimCompare(const char* a, const char* b) {
    const char* x = a;
    const char* y = b;
    for (; *x && *y; x++, y++) {
        int cx = (*x >= 'A' && *x <= 'Z') ? *x + 32 : (unsigned char)*x, cy = (*y >= 'A' && *y <= 'Z') ? *y + 32 : (unsigned char)*y;
        if (cx != cy) return cx - cy;
    }
    if (*x || *y) return *x ? 1 : -1;
    for (x = a, y = b; *x; x++, y++) if (*x != *y) return (*x >= 'a' && *x <= 'z') ? -1 : 1;
    return 0;
}
FIRE_FN void unitDimOrder(int* order) {
    int i, j;
    for (i = 0; i < FIRE_NDIMS; i++) order[i] = i;
    if (FIRE_LIKELY(!g_dimsAdded)) return;
    for (i = 1; i < FIRE_NDIMS; i++) {
        int x = order[i];
        for (j = i; j > 0 && unitDimCompare(g_dimNames[order[j - 1]], g_dimNames[x]) > 0; j--) order[j] = order[j - 1];
        order[j] = x;
    }
}
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
        int numerators = 0, denominators = 0, i, o, order[FIRE_NDIMS];
        char tmp[16];
        unitDimOrder(order);
        for (o = 0; o < FIRE_NDIMS; o++) {
            i = order[o];
            if (dims[i] > 0) { if (numerators++) fire_put(&t, "*"); fire_put(&t, g_dimNames[i]); if (dims[i] != 1) { snprintf(tmp, sizeof tmp, "^%d", (int)dims[i]); fire_put(&t, tmp); } }
        }
        if (!numerators) fire_put(&t, "1");
        for (i = 0; i < FIRE_NDIMS; i++) if (dims[i] < 0) denominators++;
        if (denominators) {
            int k = 0;
            fire_put(&t, "/");
            if (denominators > 1) fire_put(&t, "(");
            for (o = 0; o < FIRE_NDIMS; o++)
                if (dims[i = order[o]] < 0) { if (k++) fire_put(&t, "*"); fire_put(&t, g_dimNames[i]); if (dims[i] != -1) { snprintf(tmp, sizeof tmp, "^%d", (int)-dims[i]); fire_put(&t, tmp); } }
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

/* A new unit at the end of the table. */
FIRE_FN uint32_t unitAppend(const int32_t* dims, double scale, const char* display, double offset, int32_t conv, int32_t ulconv) {
    UnitDef* d;
    if (FIRE_UNLIKELY(g_udCount >= FIRE_UNIT_MAX)) fatal("Too many units.");
    if (g_udCount == g_udCap) {
        g_udCap = g_udCap ? g_udCap * 2 : 32;
        g_ud = (UnitDef*)realloc(g_ud, sizeof(UnitDef) * g_udCap);
        if (!g_ud) outOfMemory();
    }
    d = &g_ud[g_udCount];
    memcpy(d->dims, dims, sizeof d->dims);
    d->scale = scale;
    d->display = display;
    d->name = unitBuildName(dims, scale, display);
    d->offset = offset;
    d->conv = conv;
    d->ulconv = ulconv;
    return g_udCount++;
}

/* The index of the unit with these dimensions, scale and display text (made when it is new). */
FIRE_FN uint32_t unitIntern(const int32_t* dims, double scale, const char* display) {
    uint32_t i;
    for (i = 0; i < g_udCount; i++) {
        const UnitDef* e = &g_ud[i];
        if (e->scale == scale && e->offset == 0 && e->conv < 0 && e->ulconv < 0 && memcmp(e->dims, dims, sizeof e->dims) == 0 && ((e->display == NULL) == (display == NULL)) && (!display || strcmp(e->display, display) == 0)) return i;
    }
    return unitAppend(dims, scale, display, 0, -1, -1);
}

FIRE_FN bool unitCompatible(uint32_t a, uint32_t b) { return a == b || memcmp(g_ud[a].dims, g_ud[b].dims, sizeof g_ud[a].dims) == 0; }
/* Unit.Equals: same dimensions and the scales differ by less than 1e-12. */
FIRE_FN bool unitEq(uint32_t a, uint32_t b) {
    return a == b || (unitCompatible(a, b) && fabs(g_ud[a].scale - g_ud[b].scale) < 1e-12 && g_ud[a].offset == g_ud[b].offset && g_ud[a].conv == g_ud[b].conv && g_ud[a].ulconv == g_ud[b].ulconv);
}
/* Value.LabelledAs: two equal units that print the same (an equal unit under another name takes that name: `1dm^3:l` is `1l`). */
FIRE_FN bool unitSameLabel(uint32_t a, uint32_t b) {
    const char* x = g_ud[a].display;
    const char* y = g_ud[b].display;
    return a == b || (g_ud[a].offset == g_ud[b].offset && (x == y || (x && y && strcmp(x, y) == 0)));
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
    char text[240];
    snprintf(text, sizeof text, "Incompatible units: '%s' cannot be converted to '%s'.", g_ud[from].name, g_ud[to].name);
    fatalException("UnitMismatchException", text);
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

/* The units of fire and of the program a text may name (`"5mm".ToFloat()`, see below; the names of combined units look at them too). */
typedef struct TextUnit { const char* symbol; uint32_t unit; bool prefixable; } TextUnit;
extern FIRE_RT_DATA const TextUnit* g_textUnits;
extern FIRE_RT_DATA uint32_t g_textUnitCount;

/* ---- The names of combined units (Unit.CombinedName, the same steps): a list of symbols with powers. A product or a quotient keeps the symbols of its
 * operands (`kg*m/s^2`, `km/h`, `m*mm`), the same symbol counts once (`mm^2`); parts of one unit of fire with different prefixes that cancel (`m/mm*s`)
 * give their factor to the prefix of another part when one fits (`ms^-1`). ---- */
#define FIRE_UNIT_TERMS 16
typedef struct UnitTerm { char sym[32]; int exp; } UnitTerm;
typedef struct UnitTerms { UnitTerm t[FIRE_UNIT_TERMS]; int n; } UnitTerms;

/* A byte of a unit symbol in UTF-8 (the letters; the micro sign and the Greek mu are the only bytes from 0x80 on). */
FIRE_FN bool unitSymbolByte(unsigned char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c >= 0x80; }

/* Unit.TermsOfText: `kg*m/s^2`, `1/ms`, `m^-1` (everything after a `/` divides); false when the text is not of that form. */
FIRE_FN bool unitTermsOfText(const char* text, UnitTerms* out) {
    bool divide = false;
    size_t i = 0;
    out->n = 0;
    for (;;) {
        size_t start = i, end;
        int32_t power = 1;   /* (16-bit int: the check of its size must not overflow) */
        while (text[i] && unitSymbolByte((unsigned char)text[i])) i++;
        end = i;
        if (end == start) {
            if (divide || out->n > 0 || text[i] != '1' || text[i + 1] != '/') return false;
            i++;
        } else {
            if (end - start >= sizeof out->t[0].sym || out->n == FIRE_UNIT_TERMS) return false;
            if (text[i] == '^') {
                bool negative = false;
                size_t digits;
                i++;
                if (text[i] == '-') { negative = true; i++; }
                digits = i;
                power = 0;
                while (text[i] >= '0' && text[i] <= '9') { power = power * 10 + (text[i] - '0'); i++; if (power > 100000) return false; }
                if (i == digits) return false;
                if (negative) power = -power;
            }
            memcpy(out->t[out->n].sym, text + start, end - start);
            out->t[out->n].sym[end - start] = 0;
            out->t[out->n].exp = divide ? -power : power;
            out->n++;
        }
        if (!text[i]) return true;
        if (text[i] == '/') divide = true;
        else if (text[i] != '*') return false;
        i++;
    }
}

/* Unit.Terms: the display text of the unit, or - without one - its base symbols when the scale is 1; false when it has no such name. A unit with an offset
 * or conversion functions counts as its reference unit (Unit.LinearPart). */
FIRE_FN bool unitTermsOf(uint32_t u, UnitTerms* out) {
    const UnitDef* d = &g_ud[u];
    int i, o, order[FIRE_NDIMS];
    if (d->display && unitLinear(u)) return unitTermsOfText(d->display, out);
    if (fabs(d->scale - 1.0) > 1e-12) return false;
    out->n = 0;
    unitDimOrder(order);
    for (o = 0; o < FIRE_NDIMS; o++) {
        i = order[o];
        if (!d->dims[i]) continue;
        if (strlen(g_dimNames[i]) >= sizeof out->t[0].sym || out->n == FIRE_UNIT_TERMS) return false;
        strcpy(out->t[out->n].sym, g_dimNames[i]);
        out->t[out->n].exp = d->dims[i];
        out->n++;
    }
    return true;
}

/* Unit.MergeTerms: the powers of `more` (times `sign`) added; false when the list is full. */
FIRE_FN bool unitMergeTerms(UnitTerms* terms, const UnitTerms* more, int sign) {
    int m, k;
    for (m = 0; m < more->n; m++) {
        int e = sign * more->t[m].exp;
        for (k = 0; k < terms->n && strcmp(terms->t[k].sym, more->t[m].sym) != 0; k++) { }
        if (k == terms->n) {
            if (terms->n == FIRE_UNIT_TERMS) return false;
            terms->t[terms->n] = more->t[m];
            terms->t[terms->n].exp = e;
            terms->n++;
        } else if (terms->t[k].exp + e == 0) {
            memmove(&terms->t[k], &terms->t[k + 1], (size_t)(terms->n - k - 1) * sizeof(UnitTerm));
            terms->n--;
        } else terms->t[k].exp += e;
    }
    return true;
}

FIRE_FN bool unitPrefixableBase(const char* s) {
    return strcmp(s, "m") == 0 || strcmp(s, "g") == 0 || strcmp(s, "s") == 0 || strcmp(s, "b") == 0 || strcmp(s, "B") == 0 || strcmp(s, "l") == 0;
}
FIRE_FN bool unitPrefixFactor(fire_char16 c, double* f);
FIRE_FN bool unitPrefixAllowed(fire_char16 prefix, const char* symbol);
/* Unit.SplitBuiltIn: a prefixable unit of fire alone or with a prefix (`mm`, `kg`): the unit (`base` points into `sym`) and the factor of the prefix. */
FIRE_FN bool unitSplitBuiltIn(const char* sym, const char** base, double* prefix) {
    const unsigned char* s = (const unsigned char*)sym;
    fire_char16 c;
    const char* rest;
    uint32_t i;
    *base = sym;
    *prefix = 1;
    if (unitPrefixableBase(sym)) return true;
    if (strcmp(sym, "min") == 0 || strcmp(sym, "h") == 0 || strcmp(sym, "d") == 0) return false;
    for (i = 0; i < g_textUnitCount; i++) if (strcmp(g_textUnits[i].symbol, sym) == 0) return false;   /* (a unit of the program) */
    if (s[0] < 0x80) { c = s[0]; rest = sym + 1; }
    else if (s[0] == 0xC2 && s[1] == 0xB5) { c = 0xB5; rest = sym + 2; }
    else if (s[0] == 0xCE && s[1] == 0xBC) { c = 0x3BC; rest = sym + 2; }
    else return false;
    if (!*rest || !unitPrefixableBase(rest) || !unitPrefixFactor(c, prefix) || !unitPrefixAllowed(c, rest)) return false;
    *base = rest;
    return true;
}

/* Unit.SimplifyTerms */
FIRE_FN void unitSimplifyTerms(UnitTerms* terms) {
    static const char* const prefixText[] = { "f", "p", "n", "\xC2\xB5", "m", "c", "d", "h", "k", "M", "G" };
    static const fire_char16 prefixChar[] = { 'f', 'p', 'n', 0xB5, 'm', 'c', 'd', 'h', 'k', 'M', 'G' };
    static const double prefixFactor[] = { 1e-15, 1e-12, 1e-9, 1e-6, 1e-3, 1e-2, 1e-1, 1e2, 1e3, 1e6, 1e9 };
    const char* bases[FIRE_UNIT_TERMS];
    double prefixes[FIRE_UNIT_TERMS], factor = 1;
    bool drop[FIRE_UNIT_TERMS], any = false;
    int k, j, p;
    for (k = 0; k < terms->n; k++) {
        drop[k] = false;
        if (!unitSplitBuiltIn(terms->t[k].sym, &bases[k], &prefixes[k])) bases[k] = NULL;
    }
    for (k = 0; k < terms->n; k++) {
        int sum = 0, count = 0;
        if (!bases[k] || drop[k]) continue;
        for (j = k; j < terms->n; j++) if (bases[j] && strcmp(bases[j], bases[k]) == 0) { sum += terms->t[j].exp; count++; }
        if (sum != 0 || count < 2) continue;
        for (j = k; j < terms->n; j++)
            if (bases[j] && strcmp(bases[j], bases[k]) == 0) { drop[j] = true; factor *= pow(prefixes[j], terms->t[j].exp); }
        any = true;
    }
    if (!any) return;
    for (k = 0; k < terms->n; k++) {
        double wanted;
        char sym[32];
        bool found = false;
        UnitTerms kept, result;
        if (drop[k] || !bases[k]) continue;
        wanted = prefixes[k] * pow(factor, 1.0 / terms->t[k].exp);
        if (fabs(wanted - 1) <= 1e-9) { snprintf(sym, sizeof sym, "%s", bases[k]); found = true; }
        for (p = 0; p < 11 && !found; p++)
            if (fabs(wanted / prefixFactor[p] - 1) <= 1e-9 && unitPrefixAllowed(prefixChar[p], bases[k])) { snprintf(sym, sizeof sym, "%s%s", prefixText[p], bases[k]); found = true; }
        if (!found) continue;
        kept.n = 0;
        for (j = 0; j < terms->n; j++) {
            if (drop[j]) continue;
            kept.t[kept.n] = terms->t[j];
            if (j == k) snprintf(kept.t[kept.n].sym, sizeof kept.t[kept.n].sym, "%s", sym);
            kept.n++;
        }
        result.n = 0;
        unitMergeTerms(&result, &kept, 1);
        *terms = result;
        return;
    }
}

/* Unit.RenderTerms: `kg*m/s^2`, `ms^-1` when nothing is multiplied; a new text (malloc), null for no terms. */
FIRE_FN char* unitRenderTerms(const UnitTerms* terms) {
    fire_TextBuf t;
    char tmp[16];
    char* out;
    int k, positives = 0;
    bool first = true;
    if (terms->n == 0) return NULL;
    t.n = 0;
    for (k = 0; k < terms->n; k++) if (terms->t[k].exp > 0) positives++;
    if (!positives) {
        for (k = 0; k < terms->n; k++) { if (k) fire_put(&t, "*"); fire_put(&t, terms->t[k].sym); snprintf(tmp, sizeof tmp, "^%d", terms->t[k].exp); fire_put(&t, tmp); }
    } else {
        for (k = 0; k < terms->n; k++)
            if (terms->t[k].exp > 0) { if (!first) fire_put(&t, "*"); first = false; fire_put(&t, terms->t[k].sym); if (terms->t[k].exp != 1) { snprintf(tmp, sizeof tmp, "^%d", terms->t[k].exp); fire_put(&t, tmp); } }
        first = true;
        for (k = 0; k < terms->n; k++)
            if (terms->t[k].exp < 0) { fire_put(&t, first ? "/" : "*"); first = false; fire_put(&t, terms->t[k].sym); if (terms->t[k].exp != -1) { snprintf(tmp, sizeof tmp, "^%d", -terms->t[k].exp); fire_put(&t, tmp); } }
    }
    out = (char*)malloc(t.n + 1);
    if (!out) outOfMemory();
    memcpy(out, t.buf, t.n);
    out[t.n] = 0;
    return out;
}

/* Unit.CombinedName: the name of the product (`sign` 1) or the quotient (-1) of two units (a new text), null when one of them has none. */
FIRE_FN char* unitCombinedName(uint32_t a, uint32_t b, int sign) {
    UnitTerms ta, tb;
    if (!unitTermsOf(a, &ta) || !unitTermsOf(b, &tb) || !unitMergeTerms(&ta, &tb, sign)) return NULL;
    unitSimplifyTerms(&ta);
    return unitRenderTerms(&ta);
}

/* Unit.Multiply / Unit.Divide: a unit without dimensions leaves the other one as it is; the name keeps the symbols of both (`kg*m/s^2`, `mm^2`). */
FIRE_FN uint32_t unitMul(uint32_t a, uint32_t b) {
    const UnitDef* x;
    const UnitDef* y;
    int32_t dims[FIRE_NDIMS];
    char* display;
    uint32_t id;
    int i;
    if (unitIsUnitless(b)) return a;
    if (unitIsUnitless(a)) return b;
    x = &g_ud[a];
    y = &g_ud[b];
    for (i = 0; i < FIRE_NDIMS; i++) dims[i] = x->dims[i] + y->dims[i];
    display = unitDimsZero(dims) ? NULL : unitCombinedName(a, b, 1);
    id = unitIntern(dims, x->scale * y->scale, display);
    if (display && g_ud[id].display != display) free(display);   /* an equal unit existed */
    return id;
}
FIRE_FN uint32_t unitDiv(uint32_t a, uint32_t b) {
    const UnitDef* x;
    const UnitDef* y;
    int32_t dims[FIRE_NDIMS];
    char* display;
    uint32_t id;
    int i;
    if (unitIsUnitless(b)) return a;
    x = &g_ud[a];
    y = &g_ud[b];
    for (i = 0; i < FIRE_NDIMS; i++) dims[i] = x->dims[i] - y->dims[i];
    display = unitDimsZero(dims) ? NULL : unitCombinedName(a, b, -1);
    id = unitIntern(dims, x->scale / y->scale, display);
    if (display && g_ud[id].display != display) free(display);
    return id;
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Units named by a text (`"5mm".ToFloat()`, Unit.ParseKnown): the units of fire without a prefix and the units of the program (by symbol and by name) are
 * rows of a generated table (`g_textUnits`, set by the program before it runs); a prefix in front of a prefixable row and the combinations (`km/h`,
 * `kg*m/s^2`) are made here, like the VM makes them. An unknown word is no unit here.
 * --------------------------------------------------------------------------------------------------------------------- */

/* Unit.IsUnitLetter: ASCII letters, the micro sign and the Greek mu. */
FIRE_FN bool unitIsLetter(fire_char16 c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == 0xB5 || c == 0x3BC; }
/* Unit.Prefixes */
FIRE_FN bool unitPrefixFactor(fire_char16 c, double* f) {
    switch (c) {
        case 'f': *f = 1e-15; return true;
        case 'p': *f = 1e-12; return true;
        case 'n': *f = 1e-9; return true;
        case 0xB5: case 0x3BC: case 'u': *f = 1e-6; return true;
        case 'm': *f = 1e-3; return true;
        case 'c': *f = 1e-2; return true;
        case 'd': *f = 1e-1; return true;
        case 'h': *f = 1e2; return true;
        case 'k': *f = 1e3; return true;
        case 'M': *f = 1e6; return true;
        case 'G': *f = 1e9; return true;
        default: return false;
    }
}
/* Unit.PrefixAllowed: deci and hecto not in front of the bit and the byte. */
FIRE_FN bool unitPrefixAllowed(fire_char16 prefix, const char* symbol) {
    return !((prefix == 'd' || prefix == 'h') && (strcmp(symbol, "b") == 0 || strcmp(symbol, "B") == 0));
}
/* The UTF-8 of unit letters (and `*`, `/`, `^`, digits, `-`) into `out` (size `cap`); false when it does not fit. */
FIRE_FN bool unitTextUtf8(const fire_char16* s, uint32_t n, char* out, size_t cap) {
    size_t k = 0;
    uint32_t i;
    for (i = 0; i < n; i++) {
        fire_char16 c = s[i];
        if (c < 0x80) { if (k + 1 >= cap) return false; out[k++] = (char)c; }
        else { if (k + 2 >= cap || c >= 0x800) return false; out[k++] = (char)(0xC0 | (c >> 6)); out[k++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[k] = 0;
    return true;
}
/* unitIntern with a display text that is only borrowed: a new unit gets its own copy. */
FIRE_FN uint32_t unitInternText(const int32_t* dims, double scale, const char* display) {
    uint32_t before = g_udCount, id = unitIntern(dims, scale, display);
    if (id == before && g_udCount == before + 1) {
        size_t l = strlen(display);
        char* copy = (char*)malloc(l + 1);
        if (!copy) outOfMemory();
        memcpy(copy, display, l + 1);
        g_ud[id].display = copy;
    }
    return id;
}
/* Unit.ParseWith for one word: an exact symbol (of fire first, then of the program - the unit wins over a prefix), then a prefix in front of a
 * prefixable row. */
FIRE_FN bool unitOfWord(const fire_char16* w, uint32_t n, uint32_t* out) {
    char word[64];
    uint32_t i;
    double f;
    if (n == 0 || !unitTextUtf8(w, n, word, sizeof word)) return false;
    for (i = 0; i < g_textUnitCount; i++)
        if (strcmp(g_textUnits[i].symbol, word) == 0) { *out = g_textUnits[i].unit; return true; }
    if (n >= 2 && unitPrefixFactor(w[0], &f)) {
        const char* rest = word + (w[0] < 0x80 ? 1 : 2);
        for (i = 0; i < g_textUnitCount; i++) {
            const TextUnit* t = &g_textUnits[i];
            if (t->prefixable && strcmp(t->symbol, rest) == 0 && unitPrefixAllowed(w[0], t->symbol)) {
                *out = unitInternText(g_ud[t->unit].dims, f * g_ud[t->unit].scale, word);
                return true;
            }
        }
    }
    return false;
}
/* Unit.ParseKnown: the unit a text names after its number (`mm`, `km/h`, `kg*m/s^2`, `m^2`); a unit with an offset or conversion functions only alone. */
FIRE_FN bool unitOfText(const fire_char16* s, uint32_t n, uint32_t* out) {
    int32_t dims[FIRE_NDIMS];
    double scale = 1.0;
    bool divide = false, combined = false;
    uint32_t i, f;
    char display[128];
    int d;
    fire_char16 caret[128];
    /* (Unit.CaretExponents: the power written as digits right after a symbol, `m3`, `m/s2`, is `m^3`, `m/s^2`) */
    for (i = 1; i < n; i++) if (s[i] >= '0' && s[i] <= '9' && unitIsLetter(s[i - 1])) break;
    if (i < n) {
        uint32_t k = 0;
        for (i = 0; i < n; i++) {
            if (k + 2 >= 128) return false;
            if (i > 0 && s[i] >= '0' && s[i] <= '9' && unitIsLetter(s[i - 1])) caret[k++] = '^';
            caret[k++] = s[i];
        }
        s = caret;
        n = k;
    }
    for (i = 0; i < n; i++) {
        if (s[i] == '*' || s[i] == '/' || s[i] == '^') combined = true;
        else if (!unitIsLetter(s[i]) && !(s[i] >= '0' && s[i] <= '9') && s[i] != '-') return false;
    }
    if (!combined) {
        for (i = 0; i < n; i++) if (!unitIsLetter(s[i])) return false;
        return unitOfWord(s, n, out);
    }
    if (!unitTextUtf8(s, n, display, sizeof display)) return false;
    memset(dims, 0, sizeof dims);
    i = 0;
    for (;;) {
        uint32_t start = i, p, count;
        int power = 1;
        const UnitDef* u;
        while (i < n && unitIsLetter(s[i])) i++;
        if (i == start || !unitOfWord(s + start, i - start, &f)) return false;
        if (!unitLinear(f) || g_ud[f].ulconv >= 0) return false;
        if (i < n && s[i] == '^') {
            bool negative = false;
            uint32_t digits;
            i++;
            if (i < n && s[i] == '-') { negative = true; i++; }
            digits = i;
            power = 0;
            while (i < n && s[i] >= '0' && s[i] <= '9') power = power * 10 + (s[i++] - '0');
            if (i == digits || i - digits > 2) return false;
            if (negative) power = -power;
        }
        u = &g_ud[f];
        count = (uint32_t)(power < 0 ? -power : power);
        for (p = 0; p < count; p++) {
            /* (Unit.Divide / Unit.Multiply, the same steps: a factor without dimensions leaves the other one as it is) */
            if (divide != (power < 0)) {
                if (!unitDimsZero(u->dims)) { for (d = 0; d < FIRE_NDIMS; d++) dims[d] -= u->dims[d]; scale = scale / u->scale; }
            } else if (!unitDimsZero(u->dims)) {
                if (unitDimsZero(dims)) { memcpy(dims, u->dims, sizeof dims); scale = u->scale; }
                else { for (d = 0; d < FIRE_NDIMS; d++) dims[d] += u->dims[d]; scale = scale * u->scale; }
            }
        }
        if (i == n) break;
        if (s[i] == '/') divide = true;
        else if (s[i] != '*') return false;
        i++;
    }
    {
        /* the name of the unit (Unit.ParseCombined): the parts merged and simplified (`m*m/s/s` is `m^2/s^2`, `m/mm*s` is `ms^-1`) */
        UnitTerms written, merged;
        char* name = NULL;
        merged.n = 0;
        if (unitTermsOfText(display, &written) && unitMergeTerms(&merged, &written, 1)) {
            unitSimplifyTerms(&merged);
            name = unitRenderTerms(&merged);
        }
        *out = unitInternText(dims, scale, name ? name : display);
        free(name);
    }
    return true;
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
enum { R_Str, R_Lam, R_Bytes };

struct Ref {
    uint32_t rc;     /* number of holders; IMMORTAL for constants */
    uint8_t type;    /* R_Str / R_Lam / R_Bytes (the temporary copy of bytesArg) */
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
/* `arrElem`: the element type + 1 of a typed array for this type argument (`int`, `float`, `bool`, `char` without a unit: `new T[n]` of `List<int>` is
 * raw, OpCode.TypeArrayOfParam); 0 for any other. */
typedef struct TArg { const char* display; bool (*holds)(Value); bool isFloat; uint8_t kind; Value (*adapt)(Value); uint8_t arrElem; } TArg;
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
    FireGen gen;       /* ... and its generation */
    uint8_t okind;     /* O_Object / O_Array / O_Buffer */
    uint8_t flags;     /* bit 0: destroyed (or being destroyed) */
};

struct OwnList {
    Owned* head;
    Owned* tail;
    uint32_t mark;     /* scopes: height of the temporary pool when the scope was entered */
    uint32_t depth;    /* (unused: kept for the layout of OwnList) */
    OwnList* parent;   /* scopes: the enclosing scope (TakeUpwards; set only when the program uses it) */
    Owned* holder;     /* object lists: the object that owns the list (null for a scope) */
    OwnList* home;     /* the statement temporaries of a scope (SPEC 2.1): that scope's list (null for any other list) */
};
/* A scope's list that starts at `mark` (the height of the temporary pool) - as a value: the generated code declares its lists with it (`OwnList O = scopeList(poolMark())`;
 * C89 compilers like Open Watcom take no aggregate initializer that is not constant). */
FIRE_FN OwnList scopeList(uint32_t mark) {
    OwnList l;
    l.head = l.tail = NULL;
    l.mark = mark;
    l.depth = 0;
    l.parent = NULL;
    l.holder = NULL;
    l.home = NULL;
    return l;
}
/* The lists of the scopes of a generated function, all at once (a function optimised for size: one call instead of one per list). `kinds`: two characters per list -
 * its mark ('f' the end of the pool: a list of statement temporaries, 'p' the pool's height now, 'z' 0) and its home ('-' none, else '0' + the index of the list). */
FIRE_FN uint32_t poolMark(void);
FIRE_FN void scopeListsInit(OwnList* l, int n, const char* kinds) {
    int i;
    for (i = 0; i < n; i++) {
        char k = kinds[2 * i], h = kinds[2 * i + 1];
        l[i] = scopeList(k == 'f' ? 0xFFFFFFFFu : k == 'p' ? poolMark() : 0);
        if (h != '-') l[i].home = &l[(unsigned char)h - '0'];
    }
}
FIRE_FN void ownListInit(OwnList* l, uint32_t mark, Owned* holder) {
    l->head = l->tail = NULL;
    l->mark = mark;
    l->depth = 0;
    l->parent = NULL;
    l->holder = holder;
}

/* The element types of typed arrays (`int a[]`, `int[8] a[]`, `float[32] a[]`, `bool a[]`, `char a[]`, `byte a[]`; SPEC 8.4.2) - the same numbers as
 * Values.ArrayElem of the VM. */
enum { E_None, E_I8, E_I16, E_I32, E_I64, E_F8, E_F16, E_F32, E_F64, E_Bool, E_Char, E_U8 };

/* What a typed array has more than an untyped one. Its elements are usually packed: one C number per element (int8_t ... int64_t, float, Real, bool as
 * uint8_t, char as uint16_t) instead of a Value. `undefined` is a rarely used value of the type (the hint: INT8_MIN ..., a NaN with a fixed payload, 0xFF for
 * byte, 0xFFFF for char, 2 for bool); only when the hint is stored as a real number is a bit set in `real` (allocated then) - so reading looks at the mask only
 * when it finds the hint. When a native function asks for the Values (`arrItems`), the elements are unpacked once; the type stays and is still enforced. */
typedef struct ArrTyped {
    void* data;              /* packed: the numbers; else: the Values (when they are not the ones that follow the header) */
    uint8_t* real;           /* packed: bit i = element i holds the hint as a real number (NULL: none does) */
    Value unitText;          /* the unit demand as the messages show it ("cm", "(no unit)") */
    uint32_t unit;           /* the unit of the elements (with TU_UNIT) */
    uint8_t elem;            /* E_* */
    uint8_t packed;          /* the elements are packed numbers at `data` */
    uint8_t flags;           /* TU_*, below */
    uint8_t own;             /* AT_*: what is a block of its own (freed with the array) */
} ArrTyped;
enum { TU_UNIT = 1, TU_DIMONLY = 2, TU_ANY = 4 };   /* a unit demand; of its dimension only (`: any m`); any unit (`: any`) - the last two keep Values */
enum { AT_DATA = 1, AT_SELF = 2, AT_INLINE = 4 };   /* `data` / the ArrTyped itself are blocks of their own; AT_INLINE: the array has Values after its header */

/* Fixed-size array of Values (elements start as `undefined`). It owns the inner arrays that were made together with it (`new int[3][4]`, `[[1, 2], [3]]`), not
 * the elements that are assigned later. A typed array (`typed` not null) holds only values of its element type (see ArrTyped). */
struct Arr;
FIRE_FN Value* arrItems(struct Arr* a);
typedef struct Arr {
    Owned h;
    uint32_t length;
    OwnList* parts;
    ArrTyped* typed;
#ifdef __cplusplus
    Value* items() { return arrItems(this); }
#endif
} Arr;
FIRE_COLD FIRE_FN Value* arrUnpack(Arr* a);
/* The elements as Values. Of a packed typed array that unpacks it (once): code that knows typed arrays reads them with arrayGet/arrElemGet instead. */
FIRE_FN Value* arrItems(Arr* a) {
    if (FIRE_LIKELY(!a->typed)) return (Value*)((char*)a + FIRE_HDR(Arr));
    return a->typed->packed ? arrUnpack(a) : (Value*)a->typed->data;
}
/* The Values of an array without unpacking it: null for a packed one (which holds numbers only - nothing that is owned or counted). */
FIRE_FN Value* arrValues(Arr* a) {
    if (FIRE_LIKELY(!a->typed)) return (Value*)((char*)a + FIRE_HDR(Arr));
    return a->typed->packed ? NULL : (Value*)a->typed->data;
}

/* Byte buffer (`byte[]`). */
typedef struct Buf {
    Owned h;
    uint32_t length;   /* the number of elements: bytes, or the words of a buffer of machine words */
    uint8_t little;    /* the byte order the buffer is tagged with (metadata only: the bytes never change by themselves) */
    uint8_t elem;      /* the bytes of an element: 1, or sizeof(FireWord) for `word a[]` (SPEC 8.10; the words in the byte order of the machine) */
    uint8_t* data;     /* the bytes: right behind the header - or, in a package library, the memory of the buffer of the VM (see allocBufExternal) */
#ifdef __cplusplus
    uint8_t* bytes() { return data; }
#endif
} Buf;
FIRE_FN uint8_t* bufBytes(Buf* b) { return b->data; }
/* The size of a buffer in bytes (elements times their size). */
FIRE_FN uint32_t bufSize(const Buf* b) { return b->length * (uint32_t)b->elem; }   /* (in 32 bits: a size_t of 16-bit DOS would cut it) */
/* The words of a buffer of machine words. */
FIRE_FN FireWord* bufWords(Buf* b) { return (FireWord*)(void*)b->data; }

/* ---- Handles. A destroyed array or buffer must not be used any more (SPEC 2.5): in the checked modes a Value of an array or buffer carries the slot of its
 * header in a table of generations (`unit` = slot, `reserved` = generation); the slot of a
 * destroyed value gets a new generation, so a stale Value is recognized before the freed memory is touched. With FIRE_UNCHECKED (`#performance`) nothing
 * is checked and there is no table - unless the program takes pointers into arrays (FIRE_ELEMPTRS): those find their array in the table. */
/* FIRE_ELEMPTRS: the program takes pointers into arrays (`ref a[i]`) - they find their array in the slot table (a typed array converts what is written
 * through them), also with `#performance`, where FIRE_PTRBOUNDS (the bounds checks) is off. */
#if defined(FIRE_PTRBOUNDS) && !defined(FIRE_ELEMPTRS)
#define FIRE_ELEMPTRS 1
#endif
#if !defined(FIRE_UNCHECKED) || defined(FIRE_ELEMPTRS)
#define FIRE_SLOTS 1
#endif
#if defined(FIRE_ELEMPTRS)
#define FIRE_SLOT_HDR 1   /* the table knows the header in each slot */
#endif
#ifdef FIRE_SLOTS
/* One slot: its generation and - when the table knows it - the header of the value in it, side by side (one load for `arrOf` and the check). */
typedef struct SlotEntry {
#ifdef FIRE_SLOT_HDR
    Owned* hdr;
#endif
    FireGen gen;
} SlotEntry;
typedef struct SlotTable {
    SlotEntry* e;
    /* the generation of each slot when its value was made (or the slot was freed): a stale Value of the value that lives in the slot now - its generation
     * between `born` and `gen` - is a reference from before a `fire handover` (the value lives on in a thread under the next generation, handOverAll) */
    FireGen* born;
    uint32_t* freed;
    uint32_t count, cap, nfree, freedCap;
} SlotTable;
extern FIRE_RT_DATA SlotTable g_slots;
#define slotGen(slot) (g_slots.e[slot].gen)
#ifdef FIRE_SLOT_HDR
#define slotHdr(slot) (g_slots.e[slot].hdr)
#endif

FIRE_COLD FIRE_FN uint32_t slotGrow(void) {
    uint32_t cap = g_slots.cap ? g_slots.cap * 2 : 256;
    SlotEntry* e = (SlotEntry*)realloc(g_slots.e, cap * sizeof(SlotEntry));
    FireGen* born;
    if (!e) outOfMemory();
    memset(e + g_slots.cap, 0, (cap - g_slots.cap) * sizeof(SlotEntry));
    g_slots.e = e;
    born = (FireGen*)realloc(g_slots.born, cap * sizeof(FireGen));
    if (!born) outOfMemory();
    memset(born + g_slots.cap, 0, (cap - g_slots.cap) * sizeof(FireGen));
    g_slots.born = born;
    g_slots.cap = cap;
    return cap;
}
FIRE_FN void slotAcquire(Owned* o) {
    uint32_t slot;
    if (g_slots.nfree) slot = g_slots.freed[--g_slots.nfree];
    else {
        if (FIRE_UNLIKELY(g_slots.count >= g_slots.cap)) slotGrow();
        if (FIRE_UNLIKELY(g_slots.count >= FIRE_SLOT_MAX)) fatal("Too many objects, arrays and buffers at once.");
        slot = g_slots.count++;
    }
    o->slot = slot;
    o->gen = slotGen(slot);
#ifdef FIRE_SLOT_HDR
    slotHdr(slot) = o;
#endif
}
FIRE_FN void slotRelease(Owned* o) {
    uint32_t slot = o->slot;
    slotGen(slot) = (FireGen)((slotGen(slot) + 1) & FIRE_GEN_MASK);
    g_slots.born[slot] = slotGen(slot);
    if (g_slots.nfree == g_slots.freedCap) {
        uint32_t cap = g_slots.freedCap ? g_slots.freedCap * 2 : 256;
        uint32_t* freed = (uint32_t*)realloc(g_slots.freed, cap * sizeof(uint32_t));
        if (!freed) outOfMemory();
        g_slots.freed = freed;
        g_slots.freedCap = cap;
    }
    g_slots.freed[g_slots.nfree++] = slot;
}
/* The next generation of a slot whose value lives on (a part that `fire handover` moves into a thread): every older Value of it is dead. */
FIRE_FN void slotBump(Owned* o) {
    slotGen(o->slot) = (FireGen)((slotGen(o->slot) + 1) & FIRE_GEN_MASK);
    o->gen = slotGen(o->slot);
}
#else
FIRE_FN void slotAcquire(Owned* o) { o->slot = 0; o->gen = 0; }
FIRE_FN void slotRelease(Owned* o) { (void)o; }
#endif

/* The handle of an object, array or buffer in a Value, and back. */
FIRE_FN void leafSet(Value* r, const Owned* h) {
    r->reserved = h->gen;
    r->unit = h->slot;
    r->p = h;
}
/* The slot of an object, array or buffer (or of the array or buffer of a pointer into it: wide only). */
#define FIRE_VSLOT(v) ((v).unit)
/* The header behind a Value of an object, array or buffer. */
FIRE_FN Owned* leafOf(Value v) {
    return (Owned*)v.p;
}
/* Do two Values of objects, arrays or buffers name the same one? (A `fire handover` gives a value the next generation; the address stays: `==` is true, as in the VM.) */
FIRE_FN bool sameLeaf(Value a, Value b) {
    return a.p == b.p;
}
/* The same reference: the same object, array or buffer (sameLeaf), the same string or lambda (not the same text). */
FIRE_FN bool sameRef(Value a, Value b) {
    if (a.kind != b.kind) return false;
    if (a.kind == K_Class || a.kind == K_Array || a.kind == K_Buffer) return sameLeaf(a, b);
    return a.p == b.p;
}
/* What tells a live Value of an object, array or buffer apart (its address) - as a key. 0: none. */
FIRE_FN uintptr_t leafKey(Value v) {
    return (uintptr_t)v.p;
}

/* Is the array or buffer behind this value still alive? */
FIRE_FN bool leafAlive(Value v) {
#ifdef FIRE_UNCHECKED
    (void)v;
    return true;
#else
    return FIRE_LIKELY(slotGen(FIRE_VSLOT(v)) == FIRE_VGEN(v));
#endif
}
/* A dead Value of an object, array or buffer that still lives - in a fire thread it was handed over to (`fire handover`, handOverAll): the sender's reference
 * from before the handover. (A destroyed one is false.) */
FIRE_FN bool handedOverRef(Value v) {
#ifdef FIRE_UNCHECKED
    (void)v;
    return false;
#else
    FireGen born;
    if ((v.kind != K_Class && v.kind != K_Array && v.kind != K_Buffer) || FIRE_VSLOT(v) >= g_slots.cap) return false;
    born = g_slots.born[FIRE_VSLOT(v)];
    return ((FIRE_VGEN(v) - born) & FIRE_GEN_MASK) < ((slotGen(FIRE_VSLOT(v)) - born) & FIRE_GEN_MASK);
#endif
}

FIRE_FN Value StrV(const Str* s) { Value r; r.kind = K_String; FIRE_PAD0(r) r.reserved = 0; r.unit = 0; r.p = s; return r; }
FIRE_FN Value ArrV(Arr* a) { Value r; r.kind = K_Array; FIRE_PAD0(r) leafSet(&r, &a->h); return r; }
FIRE_FN Value BufV(Buf* b) { Value r; r.kind = K_Buffer; FIRE_PAD0(r) leafSet(&r, &b->h); return r; }

FIRE_FN bool isRef(Value v) { return ((0x50u >> v.kind) & 1u) != 0; }  /* String, Lambda (counted values) */
FIRE_FN Ref* refOf(Value v) { return (Ref*)v.p; }
FIRE_FN const Str* strOf(Value v) { return (const Str*)v.p; }
FIRE_FN Arr* arrOf(Value v) { return (Arr*)leafOf(v); }
FIRE_FN Buf* bufOf(Value v) { return (Buf*)leafOf(v); }
FIRE_FN Lam* lamOf(Value v) { return (Lam*)v.p; }
FIRE_FN Value LamV(Lam* l) { Value r; r.kind = K_Lambda; FIRE_PAD0(r) r.reserved = 0; r.unit = 0; r.p = l; return r; }

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

/* ---- Pointers into arrays and buffers (K_PtrByte, K_PtrElem; see "`ref` parameters"): the address of the element in the payload, the slot and the
 * generation of the array or buffer in the head - nothing counted, nothing on the heap. */
/* The address of the element (K_PtrByte, K_PtrElem). */
FIRE_FN void* ptrAddr(Value p) {
    return (void*)p.p;
}
/* The slot of its array or buffer. */
FIRE_FN uint32_t ptrSlot(Value p) {
    return p.unit;
}
/* A condition that only the checked modes look at. */
#ifdef FIRE_UNCHECKED
#define FIRE_UNCHECKED_OR(c) 1
#else
#define FIRE_UNCHECKED_OR(c) (c)
#endif
/* A pointer of kind `kind` to `addr` in the array or buffer `h`. */
FIRE_FN Value leafPtrV(uint8_t kind, void* addr, const Owned* h) {
    Value r;
    r.kind = kind; FIRE_PAD0(r)
    r.reserved = h->gen;
    r.unit = h->slot;
    r.p = addr;
    return r;
}
/* The same pointer, `bytes` further. */
FIRE_FN Value leafPtrMove(Value p, intptr_t bytes) {
    p.p = (const void*)((uintptr_t)p.p + (uintptr_t)bytes);
    return p;
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
        b = a.kind == K_Int && b.kind == K_Int ? IntOp1((int64_t)nearbyint(converted), ua, b) : Float((Real)converted, ua);
        return vpair(a, b);
    }
    if (a.kind == K_Int && b.kind == K_Int) {
        bool aIsFine = g_ud[ua].scale <= g_ud[ub].scale;
        Value* fine = aIsFine ? &a : &b;
        Value* coarse = aIsFine ? &b : &a;
        uint32_t uFine = aIsFine ? ua : ub, uCoarse = aIsFine ? ub : ua;
        double up = (double)coarse->i * unitFactor(uCoarse, uFine);
        if (up >= -9.2e18 && up <= 9.2e18) *coarse = IntOp1((int64_t)fire_snapWhole(up), uFine, *coarse);
        else *fine = IntOp1((int64_t)fire_snapWhole((double)fine->i * unitFactor(uFine, uCoarse)), uCoarse, *fine);
        return vpair(a, b);
    }
    factor = unitFactor(ub, ua);
    b = Float((Real)((b.kind == K_Int ? (double)b.i : (double)b.f) * factor), ua);
    return vpair(a, b);
}

FIRE_COLD FIRE_FN Value addUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (FIRE_UNLIKELY(p.a.kind == K_Undefined)) return Undef();
    if (p.a.kind == K_Int && p.b.kind == K_Int) return IntOp((int64_t)((uint64_t)p.a.i + (uint64_t)p.b.i), p.a.unit, p.a, p.b);
    return Float(toR(p.a) + toR(p.b), p.a.unit);
}
FIRE_COLD FIRE_FN Value subUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (FIRE_UNLIKELY(p.a.kind == K_Undefined)) return Undef();
    if (p.a.kind == K_Int && p.b.kind == K_Int) return IntOp((int64_t)((uint64_t)p.a.i - (uint64_t)p.b.i), p.a.unit, p.a, p.b);
    return Float(toR(p.a) - toR(p.b), p.a.unit);
}
FIRE_COLD FIRE_FN Value modUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (FIRE_UNLIKELY(p.a.kind == K_Undefined)) return Undef();
    if (p.a.kind == K_Int && p.b.kind == K_Int) {
        if (FIRE_UNLIKELY(p.b.i == 0)) return divideByZero();
        if (FIRE_UNLIKELY(p.b.i == -1)) return IntOp(0, p.a.unit, p.a, p.b);
        return IntOp(p.a.i % p.b.i, p.a.unit, p.a, p.b);
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
            if (!divide) return IntOp((int64_t)((uint64_t)x * (uint64_t)y * s), 0, a, b);
            if (FIRE_UNLIKELY(y == 0)) return divideByZero();
            xs = (int64_t)((uint64_t)x * s);
            if (FIRE_UNLIKELY(y == -1)) return IntOp((int64_t)(0 - (uint64_t)xs), 0, a, b);
            return IntOp(xs / y, 0, a, b);
        }
        inv = unitNiceFactor(1.0 / scale);
        if (inv >= 1 && inv == floor(inv) && inv < 9.2e18) {
            uint64_t s = (uint64_t)inv;
            int64_t d;
            if (!divide) return IntOp((int64_t)((uint64_t)x * (uint64_t)y) / (int64_t)s, 0, a, b);
            d = (int64_t)((uint64_t)y * s);
            if (FIRE_UNLIKELY(d == 0)) return divideByZero();
            if (FIRE_UNLIKELY(d == -1)) return IntOp((int64_t)(0 - (uint64_t)x), 0, a, b);
            return IntOp(x / d, 0, a, b);
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
    if (a.kind == K_Int && b.kind == K_Int) return IntOp((int64_t)((uint64_t)a.i * (uint64_t)b.i), u, a, b);
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
        if (FIRE_UNLIKELY(b.i == -1)) return IntOp((int64_t)(0 - (uint64_t)a.i), u, a, b);
        return IntOp(a.i / b.i, u, a, b);
    }
    return Float(toR(a) / toR(b), u);
}

/* `p + n` / `p - n` for a pointer: "n elements further" (SPEC 8.3). A pointer to a variable or a field is a pointer into an array with one element (the offset
 * is kept in `unit`); a pointer to an element of an array or a buffer (`ref a[i]`) moves along the elements. Using a pointer outside its range is an
 * IndexOutOfBoundsException (see ptrValid). */
FIRE_FN Value ptrOffset(Value p, int64_t n) {
    Value r = p;
    if (n == 0) return p;
    if (p.kind == K_PtrVar) r.unit = (uint32_t)(FIRE_UNIT_OFFSET(p) + (int32_t)n);
    else if (p.kind == K_PtrField) r.reserved = (uint16_t)((int16_t)p.reserved + (int16_t)n);   /* a variable or a field: an "array" with one element, the offset travels in `unit` */
    else r = leafPtrMove(p, (intptr_t)n * (p.kind == K_PtrByte ? 1 : (intptr_t)sizeof(Value)));
    return r;
}

/* ---- Words (SPEC 8.10; the VM: Value.WordOp): when one operand is a word, the operation is one on words - the other operand (an int, a char) is taken as a
 * word (cut to the width, two's complement), the result is a word cut to the width (it wraps); division, modulo, comparison and `>>` are unsigned; a shift
 * by the width or more gives 0. A float operand makes it a float operation (the word as its unsigned value). An int with a unit does not go with a word (raw
 * data): a UnitMismatchException. */
enum { W_ADD, W_SUB, W_MUL, W_DIV, W_MOD, W_POW, W_AND, W_OR, W_XOR, W_SHL, W_SHR };
FIRE_FN uint64_t wordBits(Value v) { return (uint64_t)v.w; }
/* The unsigned value of a word as a number of the program (a float32 with `#floatwidth 32`). */
FIRE_FN Real wordReal(Value v) { return (Real)(uint64_t)v.w; }
/* An operand of a word operation as a word; false when it is none (thrown). */
FIRE_COLD FIRE_FN bool wordOperand(Value v, int op, uint64_t* out) {
    char text[80];
    switch (v.kind) {
        case K_Word: *out = (uint64_t)v.w; return true;
        case K_Int:
            if (FIRE_UNLIKELY(!unitIsUnitless(v.unit))) { unitConflict(0, v.unit); return false; }
            *out = (uint64_t)(int64_t)v.i & FIRE_WORD_MASK; return true;
        case K_Char: *out = (uint64_t)v.i & FIRE_WORD_MASK; return true;
        default:
            if (op <= W_POW || op < 0) snprintf(text, sizeof text, "Type %s is not numeric.", coerceKindName(v.kind));
            else {
                static const char* const syms[] = {"+", "-", "*", "/", "%", "^", "&", "|", "#", "<<", ">>"};
                snprintf(text, sizeof text, "'%s' expects int, not %s.", syms[op], coerceKindName(v.kind));
            }
            fireError("ArgumentException", text);
            return false;
    }
}
FIRE_FN Value add(Value a, Value b);
FIRE_FN Value sub(Value a, Value b);
FIRE_FN Value mul(Value a, Value b);
FIRE_FN Value divide(Value a, Value b);
FIRE_FN Value modulo(Value a, Value b);
FIRE_FN Value power(Value a, Value b);
FIRE_COLD FIRE_FN Value wordOp(int op, Value a, Value b) {
    uint64_t x, y, r;
    if ((a.kind == K_Float || b.kind == K_Float) && op <= W_POW) {
        /* with a float: a float operation (the word as its unsigned value, without a unit) */
        if (a.kind == K_Word) a = Float(wordReal(a));
        if (b.kind == K_Word) b = Float(wordReal(b));
        switch ((int)op) {
            case W_ADD: return add(a, b);
            case W_SUB: return sub(a, b);
            case W_MUL: return mul(a, b);
            case W_DIV: return divide(a, b);
            case W_MOD: return modulo(a, b);
            default: return power(a, b);
        }
    }
    if (op == W_POW && b.kind == K_Int && b.i < 0 && a.kind == K_Word) return Float((Real)pow((double)(uint64_t)a.w, (double)b.i));   /* (a negative exponent: like int ^ int) */
    if (!wordOperand(a, op, &x) || !wordOperand(b, op, &y)) return Undef();
    switch ((int)op) {
        case W_ADD: r = x + y; break;
        case W_SUB: r = x - y; break;
        case W_MUL: r = x * y; break;
        case W_DIV: if (FIRE_UNLIKELY(y == 0)) return divideByZero(); r = x / y; break;
        case W_MOD: if (FIRE_UNLIKELY(y == 0)) return divideByZero(); r = x % y; break;
        case W_POW: {
            uint64_t e = y, f = x;
            r = 1;
            for (; e != 0; e >>= 1) { if (e & 1) r *= f; f *= f; }
            break;
        }
        case W_AND: r = x & y; break;
        case W_OR: r = x | y; break;
        case W_XOR: r = x ^ y; break;
        case W_SHL: r = y >= FIRE_WORD_BITS ? 0 : x << y; break;
        default: r = y >= FIRE_WORD_BITS ? 0 : x >> y; break;
    }
    return WordV(r);
}
/* A comparison with a word on one side: unsigned (an int taken as a word), with a float by value; -1, 0, 1 (2: thrown). */
FIRE_COLD FIRE_FN int wordCompare(Value a, Value b) {
    uint64_t x, y;
    if (a.kind == K_Float || b.kind == K_Float) {
        Real p = a.kind == K_Word ? wordReal(a) : toR(a), q = b.kind == K_Word ? wordReal(b) : toR(b);
        if (b.kind == K_Float && !unitIsUnitless(b.unit)) { unitConflict(0, b.unit); return 2; }
        if (a.kind == K_Float && !unitIsUnitless(a.unit)) { unitConflict(0, a.unit); return 2; }
        if (p < q) return -1;
        if (p > q) return 1;
        if (p == q) return 0;
        if (isnan(p)) return isnan(q) ? 0 : -1;
        return 1;
    }
    if (!wordOperand(a, -1, &x) || !wordOperand(b, -1, &y)) return 2;
    return x < y ? -1 : (x > y ? 1 : 0);
}
/* A float as the bits of a word (Value.DoubleToWordBits): rounded half to even; NaN is 0; below -2^63 it is -2^63, from 2^64 on all ones. */
FIRE_FN uint64_t wordOfReal(double f) {
    double r;
    if (isnan(f)) return 0;
    r = nearbyint(f);
    if (r >= 18446744073709551616.0) return ~0ull;
    if (r >= 9223372036854775808.0) return (uint64_t)r;
    if (r <= -9223372036854775808.0) return 0x8000000000000000ull;
    return (uint64_t)(int64_t)r;
}

FIRE_FN Value add(Value a, Value b) {
    if (isPtr(a) && b.kind == K_Int) return ptrOffset(a, b.i);
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return IntOp((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit, a, b);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) + toR(b), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return addUnits(a, b);
    if (a.kind == K_Word || b.kind == K_Word) return wordOp(W_ADD, a, b);
    return opFailed(a, b);
}
FIRE_FN Value ptrDiff(Value a, Value b);
FIRE_FN Value sub(Value a, Value b) {
    if (isPtr(a) && b.kind == K_Int) return ptrOffset(a, -b.i);
    if (isPtr(a) && isPtr(b)) return ptrDiff(a, b);
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return IntOp((int64_t)((uint64_t)a.i - (uint64_t)b.i), a.unit, a, b);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) - toR(b), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return subUnits(a, b);
    if (a.kind == K_Word || b.kind == K_Word) return wordOp(W_SUB, a, b);
    return opFailed(a, b);
}
FIRE_FN Value mul(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) return IntOp((int64_t)((uint64_t)a.i * (uint64_t)b.i), 0, a, b);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) * toR(b));
    } else if (isNumeric(a) && isNumeric(b)) return mulUnits(a, b);
    if (a.kind == K_Word || b.kind == K_Word) return wordOp(W_MUL, a, b);
    return opFailed(a, b);
}
FIRE_FN Value divide(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) return divideByZero();
            if (FIRE_UNLIKELY(b.i == -1)) return IntOp((int64_t)(0 - (uint64_t)a.i), 0, a, b);
            return IntOp(a.i / b.i, 0, a, b);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) / toR(b));
    } else if (isNumeric(a) && isNumeric(b)) return divUnits(a, b);
    if (a.kind == K_Word || b.kind == K_Word) return wordOp(W_DIV, a, b);
    return opFailed(a, b);
}
FIRE_FN Value modulo(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) return divideByZero();
            if (FIRE_UNLIKELY(b.i == -1)) return IntOp(0, a.unit, a, b);
            return IntOp(a.i % b.i, a.unit, a, b);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(fmod(toR(a), toR(b)), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return modUnits(a, b);
    if (a.kind == K_Word || b.kind == K_Word) return wordOp(W_MOD, a, b);
    return opFailed(a, b);
}
FIRE_FN int64_t intPower(int64_t b, int64_t e) {
    uint64_t r = 1, x = (uint64_t)b;
    while (e > 0) { if (e & 1) r *= x; e >>= 1; if (e > 0) x *= x; }
    return (int64_t)r;
}
/* `a ^ b` (SPEC 1.1): two ints give the int power (wrapping like `*`; a negative exponent is what `/` of ints gives - 1 for 1, +-1 for -1, 0 otherwise,
 * 0 a DivideByZeroException), anything with a float the float power. The units must be equal. */
FIRE_FN Value power(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind == K_Word || b.kind == K_Word)) return wordOp(W_POW, a, b);
    if (FIRE_UNLIKELY(!isNumeric(a) || !isNumeric(b))) return opFailed(a, b);
    if (FIRE_UNLIKELY(!unitEq(a.unit, b.unit))) { unitConflict(a.unit, b.unit); return Undef(); }
    if (a.kind == K_Int && b.kind == K_Int) {
        if (b.i < 0) {
            if (a.i == 0) return divideByZero();
            return IntOp(a.i == 1 ? 1 : a.i == -1 ? ((b.i & 1) ? -1 : 1) : 0, a.unit, a, b);
        }
        return IntOp(intPower(a.i, b.i), a.unit, a, b);
    }
    return Float((Real)pow((double)toR(a), (double)toR(b)), a.unit);
}
/* `a ^ b` whose target is a float (OpCode.PowerFloat): always the float power, also of two ints (`float f = 2 ^ -1` is 0.5). */
FIRE_FN Value powerF(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind == K_Word || b.kind == K_Word)) return wordOp(W_POW, a, b);
    if (FIRE_UNLIKELY(!isNumeric(a) || !isNumeric(b))) return opFailed(a, b);
    if (FIRE_UNLIKELY(!unitEq(a.unit, b.unit))) { unitConflict(a.unit, b.unit); return Undef(); }
    return Float((Real)pow((double)toR(a), (double)toR(b)), a.unit);
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
    if (unitEq(v.unit, target)) { if (unitSameLabel(v.unit, target)) return v; v.unit = target; return v; }   /* (`1dm^3:l` is `1l`) */
    /* `a:unitless`, and `a:unit` of another dimension: the number in the base unit (the dimension is taken out), labelled with the target unit */
    if (v.kind != K_Undefined && ((unitIsUnitless(target) && !unitIsUnitless(v.unit)) || !unitCompatible(v.unit, target))) {
        double inBase;
        if (!unitToBase(v.kind == K_Int ? (double)v.i : (double)v.f, v.unit, &inBase)) return Undef();
        if (v.kind == K_Int && fabs(inBase) < 9.0e15 && inBase == floor(inBase)) return IntOp1((int64_t)inBase, target, v);
        return Float((Real)inBase, target);
    }
    if (v.kind == K_Undefined) return UndefU(target);
    if (!unitLinear(v.unit) || !unitLinear(target)) {
        double converted;
        if (!unitConvert(v.kind == K_Int ? (double)v.i : (double)v.f, v.unit, target, &converted)) return Undef();
        return v.kind == K_Int ? IntOp1((int64_t)nearbyint(converted), target, v) : Float((Real)converted, target);
    }
    factor = unitFactor(v.unit, target);
    if (v.kind == K_Int) return IntOp1((int64_t)nearbyint((double)v.i * factor), target, v);
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
        case K_Class: return "Class"; case K_Lambda: return "Lambda"; case K_PtrVar: case K_PtrByte: case K_PtrElem: case K_PtrField: return "Pointer"; case K_Array: return "Array"; case K_Buffer: return "Buffer";
        case K_Word: return "Word";
        default: return "Undefined";
    }
}
/* `value!type` - int and float into each other, `undefined` into a number (Value.CoerceType); anything else is an ArgumentException. */
FIRE_COLD FIRE_FN Value coerceWrong(uint8_t kind, uint8_t target) {
    char text[96];
    snprintf(text, sizeof text, "Cannot coerce type %s to %s.", coerceKindName(kind), coerceKindName(target));
    return fireError("ArgumentException", text);
}
FIRE_FN Value coerceType(Value v, uint8_t target) {
    if (v.kind == target) { if (target == K_Int) v.reserved = 0; return v; }   /* (`c!int`: the number of an enum value) */
    /* a word as an int: the same bits as a signed number (like a cast in C); as a float its unsigned value. Into a word: wordOf */
    if (v.kind == K_Word && target == K_Int) return Int((int64_t)(uint64_t)v.w);
    if (v.kind == K_Word && target == K_Float) return Float(wordReal(v));
    if (v.kind == K_Word && target == K_Char) return Char((uint16_t)v.w);
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
    return coerceWrong(v.kind, target);
}

/* A float rounded to a small float format, round to nearest, ties to even (exactly as Value.RoundFloatToWidth): 32 = IEEE binary32, 16 = IEEE binary16
 * (half precision, largest value 65504, a larger value becomes infinity), 8 = FP8 E4M3 (OCP "E4M3FN": bias 7, 3 mantissa bits, largest value 448, no
 * infinity - a larger value and infinity saturate to +-448, NaN stays NaN). Every step is exact in double arithmetic (scaling by powers of two). */
FIRE_FN double roundFloatToWidth(double value, int bits) {
    int mantissaBits = bits == 16 ? 10 : 3, minExponent = bits == 16 ? -14 : -6, exponent;
    double max = bits == 16 ? 65504.0 : 448.0, a, quantum, steps, whole, rest, rounded;
    if (bits == 32) return (double)(float)value;
    if (isnan(value) || value == 0) return value;
    a = fabs(value);
    if (isinf(a)) return bits == 16 ? value : copysign(max, value);
    exponent = ilogb(a);
    if (exponent < minExponent) exponent = minExponent;
    quantum = ldexp(1.0, exponent - mantissaBits);
    steps = a / quantum;
    whole = floor(steps);
    rest = steps - whole;
    if (rest > 0.5 || (rest == 0.5 && fmod(whole, 2.0) == 1.0)) whole += 1;
    rounded = whole * quantum;
    if (rounded > max) rounded = bits == 16 ? (double)INFINITY : max;
    return copysign(rounded, value);
}

/* `x!int[8]`, `x!byte`, `x!float[32]`, `x!float[16]` (Value.ConvertWidth): the number cut to the width - an int in two's complement, `isUnsigned` keeps
 * 0..2^bits-1, a float rounded to float32, half precision or FP8 E4M3 (roundFloatToWidth). */
/* The int `n` cut to `bits` (two's complement; `isUnsigned`: 0..2^bits-1). */
FIRE_FN Value intToWidth(Value n, int bits, bool isUnsigned) {
    uint64_t mask, raw;
    int64_t r;
    /* (an explicit width beyond the program's int - `int[64]` with `#intwidth 32`, `int[32]` with `#intwidth 16` - is kept by the value: `wide`) */
    if (bits >= 64) return IntW(n.i, n.unit, 64);
    mask = (1ull << bits) - 1;
    raw = (uint64_t)n.i & mask;
    r = (int64_t)raw;
    if (!isUnsigned && (raw & (1ull << (bits - 1)))) r -= (int64_t)(1ull << bits);
    return IntW(r, isUnsigned ? 0 : n.unit, bits == 32 && !isUnsigned ? 32 : 0);   /* (`byte`, the unsigned width, is raw data without a unit: SPEC 8.10, like the VM) */
}

FIRE_FN Value convertWidth(Value v, int bits, bool toFloat, bool isUnsigned) {
    Value n = coerceType(v, toFloat ? K_Float : K_Int);
    if (n.kind != (toFloat ? K_Float : K_Int)) return n;   /* (the error of coerceType) */
    if (toFloat) return bits < 64 ? Float((Real)roundFloatToWidth((double)n.f, bits), n.unit) : n;
    return intToWidth(n, bits, isUnsigned);
}

/* `x!word` and a store into a `word` (OpCode.ConvertWidth with the word flag, Value.ToWord): an int cut to the width (two's complement), a float rounded
 * (wordOfReal), a char its code, `undefined` 0, a unit dropped (raw data); anything else is an ArgumentException. `implicit` (a store): an int is
 * converted, `undefined` stays, anything else (a float, a text, ...) is the ArgumentException - a float goes into a word only with `!word`. */
FIRE_FN Value wordOf(Value v, bool implicit) {
    char text[96];
    switch (v.kind) {
        case K_Word: return v;
        case K_Int: return WordV((uint64_t)(int64_t)v.i);
        default: break;
    }
    if (implicit) {
        if (v.kind == K_Undefined) return v;
        snprintf(text, sizeof text, "A word takes an int or a word, not %s (convert with '!word').", coerceKindName(v.kind));
        return fireError("ArgumentException", text);
    }
    switch (v.kind) {
        case K_Char: return WordV((uint64_t)v.i);
        case K_Float: return WordV(wordOfReal((double)v.f));
        case K_Undefined: return WordV(0);
        default: break;
    }
    snprintf(text, sizeof text, "Cannot coerce type %s to Word.", coerceKindName(v.kind));
    return fireError("ArgumentException", text);
}

FIRE_FN Value negate(Value v) {
    if (v.kind == K_Int) return IntOp1((int64_t)(0 - (uint64_t)v.i), v.unit, v);
    if (v.kind == K_Word) return WordV(0 - (uint64_t)v.w);   /* (wraps: -1 as a word is the largest one) */
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

#define FIRE_BITOP(NAME, OP, SYM, WOP)                                                         \
    FIRE_FN Value NAME(Value a, Value b) {                                                     \
        if (FIRE_UNLIKELY(a.kind == K_Word || b.kind == K_Word)) return wordOp(WOP, a, b);     \
        if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) return intOpFailed(SYM, a, b);   \
        if (FIRE_UNLIKELY(!unitEq(a.unit, b.unit))) { unitConflict(a.unit, b.unit); return Undef(); } \
        if (FIRE_UNLIKELY(a.reserved != 0) && a.reserved == b.reserved && enumOf(a) && enumOf(a)->flags) return EnumV(a.i OP b.i, a.reserved); \
        return IntOp(a.i OP b.i, a.unit, a, b);                                                \
    }
FIRE_BITOP(bitAnd, &, "&", W_AND)
FIRE_BITOP(bitOr, |, "|", W_OR)
FIRE_BITOP(bitXor, ^, "#", W_XOR)
#undef FIRE_BITOP
FIRE_FN Value shl(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind == K_Word || b.kind == K_Word)) return wordOp(W_SHL, a, b);
    if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) return intOpFailed("<<", a, b);
    return IntOp1((int64_t)((uint64_t)a.i << (b.i & 63)), a.unit, a);
}
FIRE_FN Value shr(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind == K_Word || b.kind == K_Word)) return wordOp(W_SHR, a, b);
    if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) return intOpFailed(">>", a, b);
    return IntOp1(a.i >> (b.i & 63), a.unit, a);
}
FIRE_FN Value bitNot(Value v) {
    if (FIRE_UNLIKELY(v.kind == K_Word)) return WordV(~(uint64_t)v.w);
    if (FIRE_UNLIKELY(v.kind != K_Int)) return intOpFailed("~", v, v);
    if (FIRE_UNLIKELY(v.reserved != 0) && enumOf(v) && enumOf(v)->flags) return EnumV(~v.i, v.reserved);   /* (the complement of a flag value is one too) */
    return IntOp1(~v.i, v.unit, v);
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Comparison
 * --------------------------------------------------------------------------------------------------------------------- */
/* -1, 0, 1 like Value.Compare (NaN sorts below everything, like double.CompareTo). */
FIRE_FN int compareNumeric(Value a, Value b) {
    Real x, y;
    if (a.kind == K_Int && b.kind == K_Int) return a.i < b.i ? -1 : (a.i > b.i ? 1 : 0);
    x = toR(a); y = toR(b);
    /* (NaN first: on the 8087 of Open Watcom a comparison with NaN says "less" and "equal" at once) */
    if (FIRE_UNLIKELY(isnan(x) || isnan(y))) return isnan(x) ? (isnan(y) ? 0 : -1) : 1;
    if (x < y) return -1;
    if (x > y) return 1;
    return 0;
}
/* The order of two floats of the generator's scalars, like compareNumeric (NaN below everything, like double.CompareTo in the VM): -1, 0 or 1. */
FIRE_FN int fire_cmpReal(Real x, Real y) {
    if (FIRE_UNLIKELY(isnan(x) || isnan(y))) return isnan(x) ? (isnan(y) ? 0 : -1) : 1;
    return x < y ? -1 : (x > y ? 1 : 0);
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
    if (a.kind == K_Word || b.kind == K_Word) return wordCompare(a, b);
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
        case K_Word: return a.w == b.w;
        case K_Float: return isnan(a.f) ? isnan(b.f) : FIRE_CC_FEQ(a.f, b.f);
        case K_Undefined: return true;
        case K_String: {
            const Str* x = strOf(a);
            const Str* y = strOf(b);
            return x == y || (x->length == y->length && memcmp(x->data, y->data, x->length * sizeof(fire_char16)) == 0);
        }
        case K_PtrVar: return a.p == b.p && a.unit == b.unit;
        case K_PtrField: return a.p == b.p && a.reserved == b.reserved;
        case K_PtrByte: case K_PtrElem: return ptrAddr(a) == ptrAddr(b);
        case K_Class: case K_Array: case K_Buffer: return sameLeaf(a, b);
        default: return a.p == b.p;
    }
}
/* Value.OperatorEquals - the `==` of the language: numbers compare by value (`1 == 1.0`), compatible units are converted (`1m == 100cm`). */
FIRE_FN bool opEq(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind == K_Word || b.kind == K_Word) && (isNumeric(a) || a.kind == K_Word) && (isNumeric(b) || b.kind == K_Word)) {
        /* a word and a number by value: an int taken as a word (`w == -1` for the largest word), a float by its value */
        if (a.kind == K_Float || b.kind == K_Float) return FIRE_CC_FEQ((a.kind == K_Word ? wordReal(a) : toR(a)), (b.kind == K_Word ? wordReal(b) : toR(b)));
        return ((uint64_t)(a.kind == K_Word ? a.w : (uint64_t)(int64_t)a.i) & FIRE_WORD_MASK) == ((uint64_t)(b.kind == K_Word ? b.w : (uint64_t)(int64_t)b.i) & FIRE_WORD_MASK);
    }
    if (!isNumeric(a) || !isNumeric(b)) return eq(a, b);
    if (a.unit != b.unit && !unitEq(a.unit, b.unit) && unitCompatible(a.unit, b.unit)) { VPair p = alignUnits(a, b); a = p.a; b = p.b; }
    if (a.kind == K_Int && b.kind == K_Int) return a.i == b.i;
    return FIRE_CC_FEQ(toR(a), toR(b));
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
    uint16_t nfields;  /* the Value slots of the fields */
    uint16_t nraw;     /* the bytes of its packed fields behind them (objRaw; docs/spec/NATIVE_BACKEND.md "Gepackte Objekte"), 0 for most */
    /* what this object owns and the type arguments it was created with - in one block, made when it first owns something or gets type arguments (objExt;
     * most objects never do), NULL until then (docs/spec/NATIVE_BACKEND.md "Der Objektkopf") */
    struct ObjExt* ext;
#ifdef __cplusplus
    Value* fields() { return (Value*)((char*)this + FIRE_HDR(Obj)); }
#endif
} Obj;
FIRE_FN Value* objFields(Obj* o) { return (Value*)((char*)o + FIRE_HDR(Obj)); }
/* The packed fields of an object (`defined int n`: a C number, laid out by the generator - CppGenerator.Packed.cs): right behind its Value slots. */
FIRE_FN char* objRaw(Obj* o) { return (char*)objFields(o) + (size_t)o->nfields * sizeof(Value); }
/* ---- Narrow fields (docs/spec/NATIVE_BACKEND.md "Schmale Felder"): a field declared `int` (with `#intwidth 32`/`16`), `float` (with `#floatwidth 32`) or
 * `bool` holds nothing but a number or a bool of that kind, or `undefined` - 8 bytes are enough (kind, a unit of 16 bits, 32 bits of payload), and two such
 * fields share the slot of one Value (the generator lays them out per class: CppGenerator.LayoutOf). Half 0 lies over the head of the slot's Value, so the
 * slot reads as a number there - every generic walk over the fields of an object (release, ownership, copy, handover) sees a value that holds no reference
 * and leaves it alone. Half 1 lies over the payload; its kind is stored XOR K_Undefined, so that the zeros of a fresh slot (Undef()) read as undefined. */
typedef struct NValue { uint8_t kind; uint8_t pad; uint16_t unit; union { int32_t i; float f; } u; } NValue;
FIRE_STATIC_ASSERT(sizeof(NValue) == 8, "A narrow field takes 8 bytes");
FIRE_FN Value nvGet(const Value* slot, int half) {
    NValue n;
    Value r;
    memcpy(&n, (const char*)slot + (half ? 8 : 0), sizeof n);
    if (half) n.kind ^= K_Undefined;
    r.kind = n.kind; r.wide = 0; r.reserved = 0; r.unit = n.unit;
    if (n.kind == K_Float) r.f = (Real)n.u.f;
    else r.i = n.kind == K_Undefined ? 0 : (int64_t)n.u.i;
    return r;
}
/* Stores `v` (of the kind of the field or undefined: the declaration checked it) - an int cut to the program's width, like a store into `int[32]`. */
FIRE_FN void nvSet(Value* slot, int half, Value v) {
    NValue n;
    if (FIRE_UNLIKELY(v.unit > 0xFFFFu)) fatal("Too many units for a narrow field.");
    n.kind = v.kind; n.pad = 0; n.unit = (uint16_t)v.unit;
    if (v.kind == K_Float) n.u.f = (float)v.f;
    else n.u.i = v.kind == K_Undefined ? 0 : (int32_t)FIRE_INT_CUT(v.i);
    if (half) n.kind ^= K_Undefined;
    memcpy((char*)slot + (half ? 8 : 0), &n, sizeof n);
}
/* A `defined` narrow field (SPEC 8.10b, NATIVE_BACKEND.md "Felder als C-Typen"): its kind and unit never change after the field's initializer stored its zero -
 * the generated code reads and writes the number alone. */
FIRE_FN int32_t nvGetI(const Value* slot, int half) { int32_t x; memcpy(&x, (const char*)slot + (half ? 8 : 0) + offsetof(NValue, u), sizeof x); return x; }
FIRE_FN void nvPutI(Value* slot, int half, int32_t x) { memcpy((char*)slot + (half ? 8 : 0) + offsetof(NValue, u), &x, sizeof x); }
FIRE_FN float nvGetF(const Value* slot, int half) { float x; memcpy(&x, (const char*)slot + (half ? 8 : 0) + offsetof(NValue, u), sizeof x); return x; }
FIRE_FN void nvPutF(Value* slot, int half, float x) { memcpy((char*)slot + (half ? 8 : 0) + offsetof(NValue, u), &x, sizeof x); }
/* Where a field lives in an object (FirePlace; for those that do not know the layout by heart - JSON, the debugger): a Value slot, the half of a narrow slot
 * (half >= 0), or a packed C number behind the slots (raw: its kind - FIRE_RAW_INT FireInt, _REAL Real, _BOOL a byte, _WORD FireWord - at byte `off`
 * of objRaw, in the static unit `unit`). */
typedef struct FirePlace { uint16_t slot; int8_t half; uint8_t raw; uint16_t off; uint32_t unit; } FirePlace;
#define FIRE_RAW_INT 1
#define FIRE_RAW_REAL 2
#define FIRE_RAW_BOOL 3
#define FIRE_RAW_WORD 4
/* The packed fields, as Values. */
FIRE_FN Value rawGetAt(const char* r, uint8_t raw, uint32_t unit) {
    switch (raw) {
        case FIRE_RAW_INT: { FireInt x; memcpy(&x, r, sizeof x); return Int(x, unit); }
        case FIRE_RAW_REAL: { Real x; memcpy(&x, r, sizeof x); return Float(x, unit); }
        case FIRE_RAW_BOOL: return Bool(*r != 0);
        default: { FireWord x; memcpy(&x, r, sizeof x); return WordV((uint64_t)x); }
    }
}
FIRE_FN Value rawGet(Obj* o, uint8_t raw, uint16_t off, uint32_t unit) { return rawGetAt(objRaw(o) + off, raw, unit); }
/* Stores a Value of the field's kind (its declaration has checked it) into a packed field. */
FIRE_FN void rawSetAt(char* r, uint8_t raw, Value v) {
    switch (raw) {
        case FIRE_RAW_INT: { FireInt x = (FireInt)v.i; memcpy(r, &x, sizeof x); break; }
        case FIRE_RAW_REAL: { Real x = v.kind == K_Float ? (Real)v.f : (Real)v.i; memcpy(r, &x, sizeof x); break; }
        case FIRE_RAW_BOOL: *r = (char)(v.i != 0); break;
        default: { FireWord x = (FireWord)v.w; memcpy(r, &x, sizeof x); break; }
    }
}
FIRE_FN void rawSet(Obj* o, uint8_t raw, uint16_t off, Value v) { rawSetAt(objRaw(o) + off, raw, v); }
/* `&obj.n` of a field declared `defined` (SPEC 8.10b): a K_PtrField - its payload is the address of the number (a packed C number, or the Value slot), `unit` the
 * number of the descriptor of the field (its name, how it is stored, its declaration; each module has its own, the runtime numbers them when they are first
 * used - under the GIL), `reserved` the offset of pointer arithmetic (only 0 can be used). What goes through it is checked like an assignment to the field. */
typedef struct FireFieldPtr { const char* name; uint8_t raw; uint8_t declared; uint8_t unitless; uint8_t hasUnit; uint32_t unit; Value text; uint32_t id; } FireFieldPtr;   /* (unit: the one it demands - hasUnit; text: that unit for the messages) */
extern FIRE_RT_DATA FireFieldPtr** g_fieldPtrs;
extern FIRE_RT_DATA uint32_t g_fieldPtrCount, g_fieldPtrCap;
FIRE_FN uint32_t fieldPtrId(FireFieldPtr* d) {
    if (FIRE_UNLIKELY(d->id == 0)) {
        if (g_fieldPtrCount == g_fieldPtrCap) {
            uint32_t cap = g_fieldPtrCap ? g_fieldPtrCap * 2 : 16;
            FireFieldPtr** items = (FireFieldPtr**)realloc(g_fieldPtrs, cap * sizeof(FireFieldPtr*));
            if (!items) outOfMemory();
            g_fieldPtrs = items;
            g_fieldPtrCap = cap;
        }
        g_fieldPtrs[g_fieldPtrCount++] = d;
        d->id = g_fieldPtrCount;
    }
    return d->id;
}
FIRE_FN Value fieldPtrV(void* addr, FireFieldPtr* d) { Value r; r.kind = K_PtrField; FIRE_PAD0(r) r.reserved = 0; r.unit = fieldPtrId(d); r.p = addr; return r; }
FIRE_FN const FireFieldPtr* fieldPtrOf(Value p) { return g_fieldPtrs[p.unit - 1]; }
FIRE_FN Value placeGet(Obj* o, const FirePlace* p) {
    if (p->raw) return rawGet(o, p->raw, p->off, p->unit);
    if (p->half >= 0) return nvGet(&objFields(o)[p->slot], p->half);
    return objFields(o)[p->slot];
}

/* The part of an object that most objects never need: the list of what it owns and its type arguments. */
typedef struct ObjExt { OwnList owned; TArgs targs; } ObjExt;
FIRE_FN ObjExt* objExt(Obj* o) {
    if (FIRE_UNLIKELY(!o->ext)) {
        o->ext = (ObjExt*)malloc(sizeof(ObjExt));
        if (FIRE_UNLIKELY(!o->ext)) fatal("Out of memory.");
        ownListInit(&o->ext->owned, 0, &o->h);
        o->ext->targs = NULL;
    }
    return o->ext;
}
/* The list of what the object owns - made now if it has none yet (8 % of the objects of the test programs own something: 40 bytes saved on the others). */
FIRE_FN OwnList* objOwned(Obj* o) { return &objExt(o)->owned; }
/* Does the object own `x` (directly)? */
FIRE_FN bool objOwns(const Obj* o, const Owned* x) { return o->ext != NULL && x->owner == &o->ext->owned; }
/* The first value the object owns (NULL: none). */
FIRE_FN Owned* objOwnedHead(const Obj* o) { return o->ext ? o->ext->owned.head : NULL; }
/* The type arguments the object was created with (NULL: none known), and setting them (no block for none). */
FIRE_FN TArgs objTArgs(const Obj* o) { return o->ext ? o->ext->targs : NULL; }
FIRE_FN void objSetTArgs(Obj* o, TArgs targs) { if (targs || o->ext) objExt(o)->targs = targs; }

/* A class of the program as the runtime sees it (ROADMAP 9, A0): the generated program fills a table of these (one row per class id, `g_classDescs`) instead of a
 * function with a switch over the class ids for each question. Its name, the number of fields, the field `message` (an exception that nothing catches) and the
 * whole-number field `ticks` (a TimeSpan for the time functions) - -1 when the class has none or the program does not need it -, the destructors of the class
 * and its base classes (derived class first, NULL: none), ToString() (NULL: none) and the exception types (the ids of `catch (T e)`, HandlerInfo.types) an
 * object of the class is, ending with -1 (NULL: none). */
#ifdef FIRE_MODULES
/* Modules (ROADMAP 9, docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung"): every module serves the members of its own classes itself (its dispatchers switch over
 * them); an object of a class it does not know - of a module that needs it, of the program, of a plugin - is served through the members the class's module
 * declares by name. A table of one module for one class: its methods sorted by name (strcmp) and number of arguments (one row per number a method takes, with
 * its number of parameters), its fields sorted by name; `k` is the class as the functions of that module number it. A function of a row serves the class `k`
 * of its module (and every class derived from it that does not declare the member anew). */
typedef Value (*FireMethodFn)(uint32_t k, Value self, const Value* a, OwnList* list, uint32_t caller);
typedef struct FireMethod { const char* name; uint32_t argc; uint32_t params; FireMethodFn fn; } FireMethod;
typedef Value (*FireFieldGetFn)(uint32_t k, Value v, OwnList* list, uint32_t caller);
typedef void (*FireFieldSetFn)(uint32_t k, Value v, Value x, OwnList* list, uint32_t caller);
typedef Value (*FireFieldPtrFn)(uint32_t k, Value v);
typedef struct FireField { const char* name; int32_t index; FireFieldGetFn get; FireFieldSetFn set; FireFieldPtrFn ptr; } FireField;
typedef struct FireMembers { uint32_t k; uint32_t nmethods; const FireMethod* methods; uint32_t nfields; const FireField* fields; } FireMembers;
#endif

typedef struct FireClassDesc {
    const char* name;
    uint32_t nfields;
    int32_t messageIndex;
    int32_t ticksIndex;
    void (*destroy)(Obj* o);
    Value (*toString)(Value self);
    const int32_t* excTypes;
#ifdef FIRE_MODULES
    const char* const* isa;              /* the names of the classes of its chain, the class first (NULL-terminated) */
    const char* const* ifaces;           /* the interfaces of its chain (NULL-terminated; NULL: none) */
    const FireMembers* const* members;   /* the tables of its members, the class first, then its bases (NULL-terminated; NULL: none) */
    const TArg* (*targOf)(Obj* o, const char* cls, int k);   /* the k-th type argument of its generic base `cls` (NULL: no generic class in its chain) */
#endif
    uint32_t flags;   /* 1: `[NoCopy]` on the class or a base class - flat/copy refuse to duplicate it */
} FireClassDesc;
extern FIRE_RT_DATA const FireClassDesc* g_classDescs;
extern FIRE_RT_DATA uint32_t g_classDescCount;
#ifdef FIRE_MODULES
/* The members of an object by name, for an object of a class the calling module does not serve (defined with the runtime's state). */
FIRE_RT_FN const FireMethod* fireFindMethod(uint32_t cls, const char* name, int argc, uint32_t* k);
FIRE_RT_FN const FireMethod* fireFindValueMethod(Value self, const char* name, int argc, uint32_t* k);
FIRE_RT_FN Value fireCallMethod(Value self, const char* name, int argc, const Value* a, OwnList* list, uint32_t caller);
FIRE_RT_FN Value fireNoMethod(Value self, const char* name, int argc);
FIRE_RT_FN Value fireGetField(Value v, const char* name, OwnList* list, uint32_t caller);
FIRE_RT_FN void fireSetField(Value v, Value x, const char* name, OwnList* list, uint32_t caller);
FIRE_RT_FN Value fireFieldPtr(Value v, const char* name);
FIRE_RT_FN bool fireIsOf(uint32_t cls, const char* name);
FIRE_RT_FN bool fireClassIsSub(uint32_t cls, const char* name);
FIRE_RT_FN int fireClassScore(uint32_t cls, const char* name, int exact, int none);
FIRE_RT_FN const TArg* fireTArgOf(Obj* o, const char* cls, int k);
/* A probe on an object of any module (Reflect.Set): the member is written and read by its name (no access check for reading the old value of a field). */
FIRE_RT_FN void fireProbeRawSet(Value obj, const char* name, Value value, OwnList* list, uint32_t caller);
FIRE_RT_FN Value fireProbeRawGet(Value obj, const char* name, OwnList* list);

/* ---- fire plugins (ROADMAP 9, phase D b; docs/spec/NATIVE_BACKEND.md "Plugins"): a library module built on its own (`--linkage plugin`), loaded while the program
 * runs (`Library.Load`). Its file exports `fire_module_info`; the runtime checks it against the program (the configuration of the runtime, the interfaces of the
 * modules it needs - ROADMAP 9.6: the hashes only), gives its classes the next ids (g_classDescs grows), maps its catch types and enums to the program's by name and
 * runs its static initialisers. Only a program with linkage "shared" can load one: it fills g_pluginHost. ---- */
#define FIRE_PLUGIN_FORMAT 5
/* A plugin that calls a native in C of the program (`fire_cn_<function>`) the program does not export: what Load says (the function: %.*s). */
#define FIRE_PLUGIN_CNATIVE "it calls the native function '%.*s' in C, which this program does not export to plugins (natives in C are the program's: build the program again with the package of the native; of graphics and windows it exports only those its libraries call)."
typedef struct FirePlugin {
    uint32_t format;                     /* FIRE_PLUGIN_FORMAT */
    const char* name;                    /* its module */
    const char* head;                    /* the configuration of the runtime it is built for (the hash of the head of its file) */
    uint32_t ndeps;
    const char* const* depNames;         /* the modules it needs and their interfaces it was built against ("format:hash") */
    const char* const* depAbis;
    uint32_t nclasses;
    const char* const* classNames;       /* its classes, in the order of their ids */
    uint32_t* base;                      /* fire_mb_<p>: the id of its first class (set when it is registered) */
    void (*fillClasses)(FireClassDesc* rows);   /* the descriptors of its classes (after *base is set) */
    uint32_t nenums;
    const FireEnum* enums;               /* its enums - by name the program's number (fire_enums_<p>[k + 1]) */
    uint8_t* enumMap;
    uint32_t nexc;
    const char* const* excNames;         /* its catch types - by name the program's id (fire_exct_<p>) */
    int32_t* excMap;
    const void* const* rfRows;           /* its reflection rows (const RfClass*, one per class; NULL: none) */
    void (*init)(void);                  /* the static initialisers of its classes */
    const char* (*link)(const void* (*find)(const char*));   /* Windows: fills its pointers to the program (NULL elsewhere); the name the program lacks */
    uint32_t nunits;
    const UnitInit* units;               /* its units (row 0: none), their dimensions over dimNames - by dimensions, scale and symbol the program's (unitMap) */
    uint16_t* unitMap;                   /* fire_units_<p> */
    uint32_t ndims;
    const char* const* dimNames;         /* the dimensions of its units, by name the program's (a new one in a free place of g_dimNames) */
    uint32_t nconv;
    const UnitConvFn* convTo;            /* the conversion functions of its units (UnitInit.conv/ulconv) */
    const UnitConvFn* convFrom;
    const uint8_t* convOwn;              /* 1: of a unit the plugin declares (always a new unit), 0: of a library (the program's unit when it has it) */
    uint32_t ntext;
    const TextUnit* textUnits;           /* the units a text may name (`"5in".ToFloat()`), with its numbers: those the program does not know are added */
    /* JSON (the program has `#import "json"`; NULL: it has not): the JsonClassInfo of its class k (fire_json.h; the units of the fields are the program's once
     * it is registered), the k of its class of a name (-1: none), the texts of its type arguments and back (NULL: not its own) */
    const void* (*jsonAt)(uint32_t k);
    int32_t (*jsonNamed)(const char* name);
    const char* (*jsonTargText)(const TArg* t);   /* (not jsonTArgText: a DLL defines that name as a macro) */
    const TArg* (*jsonTargFor)(const char* text);
    /* its `class extends` of classes of the program and of libraries, and of base types (`$string`, ...): the class by name and the table of the methods it adds */
    uint32_t nexts;
    const char* const* extClasses;
    const FireMembers* const* extTables;
    /* a debug build (0/NULL otherwise; docs/spec/NATIVE_DEBUG_PROTOCOL.md "sources"): its sources (file, hash) in the order of fire_dbgsrc_<p>, which the runtime fills with
     * their indexes after the program's (the debugger hears them), and its class k for the debugger (const DbgClass*: the names of its fields) */
    uint32_t nsources;
    const char* const* srcFiles;
    const char* const* srcHashes;
    int32_t* srcMap;
    const void* (*dbgClass)(uint32_t k);
} FirePlugin;
/* What a program that can load plugins tells the runtime (linkage "shared"; NULL: it cannot). */
typedef struct FirePluginHost {
    const char* head;                                /* the hash of the head of its files */
    const char* (*moduleAbi)(const char* module);    /* the interface ("format:hash") of a library module of the program, NULL: none of that name */
    const char* const* excNames;                     /* its catch types by name (index = id) */
    uint32_t nexc;
    const void* (*find)(const char* name);           /* Windows: what a DLL may use of the program */
} FirePluginHost;
extern FIRE_RT_DATA const FirePluginHost* g_pluginHost;
/* The reflection rows of the plugins' classes (const RfClass*; the program's rf_find looks here after its own). */
extern FIRE_RT_DATA const void** g_pluginRf;
extern FIRE_RT_DATA uint32_t g_pluginRfCount;
/* Registers a plugin (once; again: the same answer): an array of the names of its classes, or a string that says why it does not fit. */
FIRE_RT_FN Value fireRegisterPlugin(const FirePlugin* p, OwnList* list);
/* JSON of the classes of the plugins (the program's jsonClassAt, ... ask here for what is not in its tables): the JsonClassInfo of a class id (NULL: no class of a
 * plugin, or none with JSON), the id of a class name (-1: none), the text of a type argument of a plugin and back (NULL: none). */
FIRE_RT_FN const void* firePluginJsonAt(uint32_t cls);
FIRE_RT_FN int32_t firePluginJsonNamed(const char* name);
FIRE_RT_FN const char* firePluginTArgText(const TArg* t);
FIRE_RT_FN const TArg* firePluginTArgFor(const char* text);
#endif
/* The catch type `Exception` (catches every exception object): always type id 0. */
#define FIRE_EXC_ANY 0

/* Runs the destructors of the object's class chain, derived class first (the descriptor of its class). */
FIRE_RT_FN void runDestructors(Obj* o);

FIRE_FN Value ObjV(Obj* o) { Value r; r.kind = K_Class; FIRE_PAD0(r) leafSet(&r, &o->h); return r; }
FIRE_FN Obj* asObj(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Class)) fatal("A member was accessed on something that is not an object.");
    return (Obj*)leafOf(v);
}
/* The object of a value the generator proved to be one (an object of a known class). */
FIRE_FN Obj* objAt(Value v) { return (Obj*)leafOf(v); }

#ifdef FIRE_THREADS
extern FIRE_RT_DATA bool g_domainOn;   /* a fire thread exists: the objects of the global scope are the shared domain (flag 64) */
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
/* The bytes of a block: a header of `head` bytes and `count` elements of `elem` bytes - computed wide. A block that does not fit a size_t (16-bit DOS: 64 KB)
 * is an error, never a smaller block. (On 32 and 64 bits the compiler drops the check where it cannot happen.) */
FIRE_FN size_t blockSize(size_t head, uint32_t count, size_t elem) {
    uint64_t n = (uint64_t)head + (uint64_t)count * (uint64_t)elem;
    if (FIRE_UNLIKELY(n > (uint64_t)SIZE_MAX - 8)) fatal("Out of memory: a block larger than this machine can address at once (64 KB in real mode).");
    return (size_t)n;
}

/* The longest value of an environment variable that Environment.Get reads (16-bit DOS: a block holds less than 64 KB). */
#if SIZE_MAX > 0xFFFFu
#define FIRE_ENV_VALUE_MAX 65536
#else
#define FIRE_ENV_VALUE_MAX 4096
#endif

/* The block of an array or a buffer (with its data): it may pass what a size_t holds where the compiler has bigger blocks (16-bit real mode, the huge model:
 * FIRE_CC_BIG_ALLOC - halloc); freed with dataFree. Elsewhere malloc and free. */
FIRE_FN void* dataAlloc(size_t head, uint32_t count, size_t elem) {
#ifdef FIRE_CC_BIG_ALLOC
    uint64_t n = (uint64_t)head + (uint64_t)count * (uint64_t)elem;
#ifndef FIRE_CC_BIG_FROM
#define FIRE_CC_BIG_FROM ((uint64_t)SIZE_MAX - 8)
#endif
    if (n > (uint64_t)FIRE_CC_BIG_FROM) {   /* (a block the C library cannot hold - or, where the dialect says so, one it holds badly) */
        void* p;
        if (FIRE_UNLIKELY(n > 0x7FFFFFF0u)) fatal("Out of memory: a block larger than 2 GB.");
        p = FIRE_CC_BIG_ALLOC((uint32_t)n);
        if (p != NULL || n > (uint64_t)SIZE_MAX - 8) return p;
    }
#endif
    return malloc(blockSize(head, count, elem));
}
FIRE_FN void dataFree(void* p) {
#ifdef FIRE_CC_BIG_FREE
    if (FIRE_CC_BIG_FREE(p)) return;   /* (a big block: given back the way it was made) */
#endif
    free(p);
}
/* Fills / copies the data of an array or a buffer - also more than a size_t holds (the dialect of a compiler with huge blocks does it in steps: FIRE_CC_ZERO,
 * FIRE_CC_COPY; the C library's memset/memcpy of the 16-bit real mode wrap at the end of a segment). */
FIRE_FN void dataZero(void* p, uint32_t bytes) {
#ifdef FIRE_CC_ZERO
    FIRE_CC_ZERO(p, bytes);
#else
    memset(p, 0, (size_t)bytes);
#endif
}
FIRE_FN void dataCopy(void* to, const void* from, uint32_t bytes) {
#ifdef FIRE_CC_COPY
    FIRE_CC_COPY(to, from, bytes);
#else
    memcpy(to, from, (size_t)bytes);
#endif
}

/* An object of the class `cls` with `fieldCount` Value slots and `rawBytes` bytes of packed fields (zero: their numbers start at 0). */
FIRE_FN Value newObjectR(uint32_t cls, uint32_t fieldCount, uint32_t rawBytes, OwnList* owner) {
    void* memory;
    Obj* o;
    Value* f;
    uint32_t i;
    if (FIRE_UNLIKELY(fieldCount > 0xFFFFu || rawBytes > 0xFFFFu)) fatal("A class with too many fields.");
    memory = malloc(blockSize(FIRE_HDR(Obj) + rawBytes, fieldCount, sizeof(Value)));
    if (FIRE_UNLIKELY(!memory)) allocFailed();
    o = (Obj*)memory;
    o->h.okind = O_Object;
    slotAcquire(&o->h);   /* the handle: a destroyed object is recognized by it (SPEC 2.3, 2.5) */
    o->cls = cls;
    o->h.flags = 0;
    o->ext = NULL;
    o->nfields = (uint16_t)fieldCount;
    o->nraw = (uint16_t)rawBytes;
    f = objFields(o);
    for (i = 0; i < fieldCount; i++) f[i] = Undef();
    if (rawBytes) memset(objRaw(o), 0, rawBytes);
    ownLink(owner, &o->h);
    return ObjV(o);
}
FIRE_FN Value newObject(uint32_t cls, uint32_t fieldCount, OwnList* owner) { return newObjectR(cls, fieldCount, 0, owner); }

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
        free(obj->ext);
        free(obj);
#endif
        o = next;
    }
}

/* After a constructor: one that ended with an exception made no object (SPEC 5.3) - its destructor does not run when it goes (flag 16, as for the copies of
 * `taking`); its fields and what it owns are freed as usual. */
FIRE_FN void ctorEnd(Value o) { if (FIRE_UNLIKELY(g_unwind.active) && o.kind == K_Class) asObj(o)->h.flags |= 16; }

FIRE_FN void destroy(Obj* o) {
    if (o->h.flags & 1) return;
    o->h.flags |= 1;
    batchBegin();
    if (o->h.flags & 16) {
        /* a copy that `taking` made for a fire thread (its original still lives), or an object whose constructor threw: no destructor */
    } else if (FIRE_UNLIKELY(g_unwind.active)) {
        /* the scope is left because of an exception: the destructor runs as ordinary code, the exception keeps unwinding afterwards */
        UnwindState saved = g_unwind;
        g_unwind.active = 0;
        runDestructors(o);
        g_unwind = saved;
    } else runDestructors(o);
    if (o->ext) destroyList(&o->ext->owned);
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
        Value* items = arrValues(a);   /* (a packed array holds numbers: nothing to release) */
        uint32_t i;
        if (items) for (i = 0; i < a->length; i++) release(items[i]);
        if (a->parts) { destroyList(a->parts); free(a->parts); }
        if (a->typed) {
            ArrTyped* t = a->typed;
            free(t->real);
            if (t->own & AT_DATA) dataFree(t->data);
            if (t->own & AT_SELF) free(t);
        }
    }
    slotRelease(o);
    dataFree(o);
}

/* Leaving a scope: destroys what it owns and lets go of the strings/arrays it created. */
FIRE_FN void leaveSlow(OwnList* list) {
    if (list->head) destroyList(list);
    if (g_pool.top > list->mark) poolRelease(list->mark);
}
/* (after nearly every statement - mostly with nothing to do: the test embedded, the work called) */
FIRE_ALWAYS_INLINE FIRE_FN void leave(OwnList* list) {
    if (FIRE_UNLIKELY(list->head != NULL) || FIRE_UNLIKELY(g_pool.top > list->mark)) leaveSlow(list);
}

/* The header of an object, an array or a buffer (null for any other value, and for an array or buffer that is already destroyed). */
FIRE_FN Owned* ownedOf(Value v) {
    if ((v.kind == K_Class || v.kind == K_Array || v.kind == K_Buffer) && leafAlive(v)) return leafOf(v);
    return NULL;
}

/* `return`: an object (array, buffer) returned from the scope that owns it does not die with it; it goes to the caller (SPEC 2.3). */
FIRE_FN void transferOut(Value v, OwnList* list) {
    Owned* o = ownedOf(v);
    if (o && o->owner == list) ownUnlink(o);
    else if (v.kind == K_Lambda) {   /* a lambda owns nothing: what it captured from `list` travels with it (the caller's `adopt` takes it) */
        Lam* l = (Lam*)v.p;
        uint32_t i;
        for (i = 0; i < l->ncaps; i++) transferOut(lamCaps(l)[i], list);
    }
}

/* The captures of a returned lambda that travel without an owner (transferOut, transferTree): `list` takes them like the lambda. */
FIRE_FN void adoptCaptures(Lam* l, OwnList* list) {
    uint32_t i;
    for (i = 0; i < l->ncaps; i++) {
        Value c = lamCaps(l)[i];
        Owned* o = ownedOf(c);
        if (o) { if (!o->owner) ownLink(list, o); }
        else if (c.kind == K_Lambda) adoptCaptures((Lam*)c.p, list);
    }
}

/* The caller takes over what came back from a call: an object, array or buffer without an owner, or a string/lambda that the callee retained for the trip (that
 * count now belongs to the scope's pool). */
/* A value with one more count: what a call gives its caller (the caller's `adopt` hands that count to the pool) - for a built-in method that made a string
 * in the pool. */
FIRE_FN Value retainedV(Value v) { retain(v); return v; }

FIRE_FN void adopt(Value v, OwnList* list) {
    Owned* o = ownedOf(v);
    if (o) {
        if (!o->owner) ownLink(list, o);
    } else if (isRef(v)) {
        Ref* r = refOf(v);
        if (r->rc != IMMORTAL) poolPush(r);   /* the count that came with the value now belongs to the pool */
        if (v.kind == K_Lambda) adoptCaptures((Lam*)v.p, list);
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
    Str* s = (Str*)malloc(blockSize(sizeof(Str), length + 1, sizeof(fire_char16)));
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
    Arr* a = (Arr*)dataAlloc(FIRE_HDR(Arr), length, sizeof(Value));
    Value* items;
    uint32_t i;
    if (FIRE_UNLIKELY(!a)) allocFailed();
    a->h.okind = O_Array;
    a->h.flags = 0;
    a->length = length;
    a->parts = NULL;
    a->typed = NULL;
    items = arrItems(a);
    for (i = 0; i < length; i++) items[i] = Undef();
    slotAcquire(&a->h);
    ownLink(list, &a->h);
    return a;
}

FIRE_FN Lam* allocLam(uint32_t nparams, uint32_t ncaps, Value (*fn)(Value, const Value*), OwnList* list) {
    Lam* l = (Lam*)malloc(blockSize(FIRE_HDR(Lam), ncaps, sizeof(Value)));
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
        snprintf(text, sizeof text, "Wrong number of arguments: expected %u%s, got %d.", (unsigned)l->nparams, (uint32_t)argc < l->nparams ? " (the missing parameters have no default value)" : "", argc);
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
/* `callee(args...)` of the generated code. */
FIRE_FN Value callLamV(Value callee, int argc, const Value* args, OwnList* list) {
    Lam* l;
    if (FIRE_UNLIKELY(callee.kind != K_Lambda)) fatal("Call of a value that is not a lambda.");
    l = lamOf(callee);
    if (FIRE_UNLIKELY(l->nparams != (uint32_t)argc)) return callLamDefaults(callee, argc, args, list);
    return l->fn(callee, args);
}
/* A lambda that the runtime or a native calls (a probe, a job, a callback): in a debug build the VM runs it nested - no stops inside. */
FIRE_FN Value callLam(Value callee, int argc, const Value* args, OwnList* list) {
#ifdef FIRE_DEBUG
    Value r;
    g_dbgNest++;
    r = callLamV(callee, argc, args, list);
    g_dbgNest--;
    return r;
#else
    return callLamV(callee, argc, args, list);
#endif
}

/* CheckLambdaSignature: a value assigned to a `lambda<...>` annotation must have that many parameters (otherwise an ArgumentException). */
FIRE_FN void checkLambda(Value v, int nparams) {
    if (FIRE_UNLIKELY(v.kind != K_Lambda || lamOf(v)->nparams != (uint32_t)nparams)) {
        char text[120];
        if (v.kind != K_Lambda) snprintf(text, sizeof text, "Expected a lambda with %d parameter(s), got a value that is no lambda.", nparams);
        else snprintf(text, sizeof text, "The lambda has %u parameter(s), its declaration says %d.", (unsigned)lamOf(v)->nparams, nparams);
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
    if (kind == K_Float && v.kind == K_Word) return Float(wordReal(v));   /* (a word as its unsigned value) */
    snprintf(text, sizeof text, "%s is %s, not %s.", what, kind == K_Int ? "an int" : kind == K_Float ? "a float" : "a bool", coerceKindName(v.kind));
    fireError("ArgumentException", text);
    return Undef();
}
FIRE_FN Value guardKind(Value v, uint8_t kind, bool unitless, const char* what, Value unitlessText) {
    if (FIRE_LIKELY(v.kind == kind) && (!unitless || v.unit == 0)) return v;
    return guardKindSlow(v, kind, unitless, what, unitlessText);
}

/* `defined int`, `defined float`, `defined bool`, `defined word`, `defined char` (SPEC 8.10b): undefined is an ArgumentException, a value of an enum becomes its number. */
FIRE_COLD FIRE_FN Value guardKindNNSlow(Value v, uint8_t kind, bool unitless, const char* what, Value unitlessText) {
    if (v.kind == K_Undefined) {
        char text[200];
        snprintf(text, sizeof text, "%s is %s and never undefined.", what,
            kind == K_Int ? "an int" : kind == K_Float ? "a float" : kind == K_Word ? "a word" : kind == K_Char ? "a char" : "a bool");
        fireError("ArgumentException", text);
        return Undef();
    }
    if (v.kind == K_Int) v.reserved = 0;
    if (kind == K_Word || kind == K_Char) {
        if (v.kind == kind) return v;
        char text[200];
        snprintf(text, sizeof text, "%s is %s, not %s.", what, kind == K_Word ? "a word" : "a char", coerceKindName(v.kind));
        fireError("ArgumentException", text);
        return Undef();
    }
    return guardKindSlow(v, kind, unitless, what, unitlessText);
}
FIRE_FN Value guardKindNN(Value v, uint8_t kind, bool unitless, const char* what, Value unitlessText) {
    if (FIRE_LIKELY(v.kind == kind) && (!unitless || v.unit == 0) && (kind != K_Int || v.reserved == 0)) return v;
    return guardKindNNSlow(v, kind, unitless, what, unitlessText);
}

/* A declaration `int` (not `int[32]`/`int[64]`): it holds the program's int - with `#intwidth 32`/`16` an int that keeps a wider width is cut when it goes in
 * (DeclaredKind.CutsWide of the VM; a narrow field stores 32 bits anyway). */
#ifdef FIRE_INT32
FIRE_FN Value intProgram(Value v) { if (v.kind == K_Int && v.wide) { v.i = FIRE_INT_CUT(v.i); v.wide = 0; } return v; }
#else
#define intProgram(v) (v)
#endif
/* A unit check of the generated code in place (CheckUnit): adaptUnit on the variable. `flags`: 1 only the dimension (`: any m`), 2 into a float. True when it threw. */
FIRE_FN Value adaptUnit(Value v, uint32_t unit, bool dimensionOnly, bool toFloat, Value expectedText);
FIRE_FN bool adaptSlot(Value* v, uint32_t unit, uint8_t flags, const Str* expectedText) {
    *v = adaptUnit(*v, unit, (flags & 1) != 0, (flags & 2) != 0, StrV(expectedText));
#ifdef FIRE_EXCEPTIONS
    return g_unwind.active;
#else
    return false;
#endif
}
/* The slow path of the check of a declared kind in the generated code (its fast path is inline: the kind, the unit, the enum, the width): the conversion or the
 * ArgumentException, in place. `flags`: 1 unitless, 2 `defined` (guardKindNN), 4 cut to the program's int. True when it threw. */
FIRE_COLD FIRE_FN bool guardSlot(Value* v, uint8_t kind, uint8_t flags, const char* what, const Str* unitlessText) {
    Value text = StrV(unitlessText);
    *v = (flags & 2) ? guardKindNN(*v, kind, (flags & 1) != 0, what, text) : guardKind(*v, kind, (flags & 1) != 0, what, text);
    if (flags & 4) *v = intProgram(*v);
#ifdef FIRE_EXCEPTIONS
    return g_unwind.active;
#else
    return false;
#endif
}
/* A float declaration that takes every unit (`#any`): a whole number becomes a float, the unit stays. */
FIRE_FN Value intToFloat(Value v) { return v.kind == K_Int ? Float((Real)v.i, v.unit) : v.kind == K_Word ? Float(wordReal(v)) : v; }

/* CheckUnit (Value.AdaptToUnit): a declaration with a unit takes `v` - a value of the same dimension is converted into `unit`, `undefined` takes it; with
 * `dimensionOnly` (`: any m`) every unit of that dimension is taken as it is; with `toFloat` (a float declaration) a whole number is converted as a float.
 * Another dimension is an UnitMismatchException, a whole number that does not fit any more after the conversion an OverflowException. Values without a unit
 * (strings, objects) are left alone. */
/* An array into a declaration with a unit (`float:km/h speeds[] = [...]`): each element (also of inner arrays) is converted where it is (Value.AdaptToUnit). */
FIRE_COLD FIRE_FN void adaptArrayUnit(Value v, uint32_t unit, bool dimensionOnly, bool toFloat, Value expectedText, int depth) {
    Value* items;
    uint32_t i, n;
    if (!leafAlive(v) || depth > 32) return;
    items = arrItems(arrOf(v));
    n = arrOf(v)->length;
    for (i = 0; i < n; i++) {
        Value e = items[i];
        if (e.kind == K_Array) adaptArrayUnit(e, unit, dimensionOnly, toFloat, expectedText, depth + 1);
        else if (e.kind == K_Int || e.kind == K_Float || e.kind == K_Undefined) {
            e = adaptUnit(e, unit, dimensionOnly, toFloat, expectedText);
            if (g_unwind.active) return;
            items[i] = e;
        }
        if (g_unwind.active) return;
    }
}

FIRE_FN Value adaptUnit(Value v, uint32_t unit, bool dimensionOnly, bool toFloat, Value expectedText) {
    double converted;
    if (FIRE_UNLIKELY(v.kind == K_Array)) { adaptArrayUnit(v, unit, dimensionOnly, toFloat, expectedText, 0); return v; }
    if (toFloat && v.kind == K_Word) v = Float(wordReal(v));   /* a word into a float declaration: its unsigned value (without a unit) */
    if (!(v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined)) return v;
    if (toFloat && v.kind == K_Int) v = Float((Real)v.i, v.unit);   /* a float declaration: a whole number becomes one (`float f = 3` holds 3.0) */
    if (unitEq(v.unit, unit)) { if (!dimensionOnly && !unitSameLabel(v.unit, unit)) v.unit = unit; return v; }   /* (printed with the unit of the declaration) */
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

/* A buffer of `length` elements of `elem` bytes (1: a byte buffer; sizeof(FireWord): a buffer of machine words), all 0. The data is aligned for a word. */
FIRE_FN Buf* allocBufOf(uint32_t length, uint8_t elem, OwnList* list) {
    size_t head = (sizeof(Buf) + 7u) & ~(size_t)7u;
    Buf* b = (Buf*)dataAlloc(head, length, elem);
    if (FIRE_UNLIKELY(!b)) allocFailed();
    b->h.okind = O_Buffer;
    b->h.flags = 0;
    b->length = length;
    b->little = hostLittle() ? 1 : 0;
    b->elem = elem;
    b->data = (uint8_t*)b + head;
    dataZero(b->data, (uint32_t)((uint64_t)length * elem));
    slotAcquire(&b->h);
    ownLink(list, &b->h);
    return b;
}
FIRE_FN Buf* allocBuf(uint32_t length, OwnList* list) { return allocBufOf(length, 1, list); }
/* A copy of a buffer (its elements, its byte order) owned by `list`. */
FIRE_FN Buf* bufCopy(const Buf* src, OwnList* list) {
    Buf* c = allocBufOf(src->length, src->elem, list);
    if (src->length) dataCopy(c->data, src->data, (uint32_t)((uint64_t)src->length * src->elem));
    c->little = src->little;
    return c;
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
    b->elem = 1;
    b->data = data;
    slotAcquire(&b->h);
    ownLink(list, &b->h);
    return b;
}
#endif

/* ---------------------------------------------------------------------------------------------------------------------
 * Strings
 * --------------------------------------------------------------------------------------------------------------------- */
/* Calls the ToString() method of the object's class if it has one (the result is a string value created in `list`; the descriptor of the class). */
FIRE_RT_FN bool userToString(Value object, OwnList* list, Value* result);

/* The name of the class with this id ("?" for none; the descriptor of the class). */
FIRE_RT_FN const char* className(uint32_t cls);

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
        else if ((c >> 3) == 30 && s[0] && s[1] && s[2]) { cp = ((uint32_t)(c & 0x07) << 18) | ((uint32_t)((unsigned char)s[0] & 0x3F) << 12) | ((uint32_t)((unsigned char)s[1] & 0x3F) << 6) | ((unsigned char)s[2] & 0x3F); s += 3; }
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

/* The text of a value of an enum (Values.EnumType.Format): the name of its member; for a flag enum the names of the flags it combines ("a, b"); otherwise the
 * number. Returns the length (at most `cap - 1`; a long list of flags is cut). */
FIRE_FN size_t enumFormat(Value v, char* out, size_t cap) {
    const FireEnum* e = enumOf(v);
    uint32_t i;
    size_t n = 0;
    int64_t rest = v.i;
    if (e) {
        for (i = 0; i < e->count; i++) if (e->members[i].value == v.i) return (size_t)snprintf(out, cap, "%s", e->members[i].name) < cap ? strlen(out) : cap - 1;
        if (e->flags && v.i != 0) {
            out[0] = 0;
            for (i = 0; i < e->count; i++) {
                int64_t f = e->members[i].value;
                if (f == 0 || (f & (f - 1)) != 0 || (v.i & f) != f) continue;
                n += (size_t)snprintf(out + n, n < cap ? cap - n : 0, "%s%s", n ? ", " : "", e->members[i].name);
                if (n >= cap) n = cap - 1;
                rest &= ~f;
            }
            if (rest == 0 && n > 0) return n;
        }
    }
    n = (size_t)snprintf(out, cap, "%lld", (long long)v.i);
    return n < cap ? n : cap - 1;
}

FIRE_FN void pieceOf(Value v, Piece* out, OwnList* list) {
    char tmp[64];
    switch (v.kind) {
        case K_String: out->p = strOf(v)->data; out->n = strOf(v)->length; return;
        case K_Char: out->buf[0] = (fire_char16)v.i; out->p = out->buf; out->n = 1; return;
        case K_Bool: setPiece(out, v.i ? "True" : "False"); return;
        case K_Int: {
            int n;
            if (FIRE_UNLIKELY(v.reserved != 0) && enumOf(v)) { char text[64]; n = (int)enumFormat(v, text, sizeof text); out->n = widenAscii(text, (uint32_t)n, out->buf); break; }
            n = snprintf(tmp, sizeof tmp, "%lld", (long long)v.i);
            out->n = widenAscii(tmp, (uint32_t)n, out->buf);
            break;
        }
        case K_Float: out->n = widenAscii(tmp, (uint32_t)formatReal(v.f, tmp), out->buf); break;
        case K_Word: out->n = widenAscii(tmp, (uint32_t)snprintf(tmp, sizeof tmp, "%llu", (unsigned long long)v.w), out->buf); break;   /* (unsigned) */
        case K_Undefined: setPiece(out, "undefined"); if (v.unit && !unitIsUnitless(v.unit)) { out->buf[out->n++] = (fire_char16)':'; } break;
        case K_Class: {
            Value text;
            text.kind = K_Undefined; FIRE_PAD0(text) text.reserved = 0; text.unit = 0; text.i = 0;   /* (the compiler cannot see that userToString sets it) */
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
        case K_PtrVar: case K_PtrByte: case K_PtrElem: case K_PtrField: setPiece(out, "<pointer>"); return;
        case K_Array: setPiece(out, "<array>"); return;
        case K_Buffer: {
            int n = snprintf(tmp, sizeof tmp, bufOf(v)->elem != 1 ? "<buffer %u words>" : "<buffer %u bytes>", (unsigned)bufOf(v)->length);
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
    if (isPtr(a) && b.kind == K_Int) return ptrOffset(a, b.i);
    if (a.kind == K_String || b.kind == K_String) return concat(a, b, list);
    if (isNumeric(a) && isNumeric(b)) return addUnits(a, b);
    if (a.kind == K_Word || b.kind == K_Word) return wordOp(W_ADD, a, b);
    return opFailed(a, b);
}

/* `+` for operands that may be strings (the generator uses `add` where both are known to be numbers). */
FIRE_FN Value addR(Value a, Value b, OwnList* list) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return IntOp((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit, a, b);
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
/* StringMethods.SplitUnit: a number followed by a unit (`"5mm"`, `"-2.5 km/h"`): the first place where a letter starts a unit text (unitOfText) and the
 * text in front of it is a number (spaces between them are allowed). */
FIRE_FN bool splitNumberUnit(const fire_char16* s, uint32_t len, bool isFloat, int64_t* whole, double* number, uint32_t* unit) {
    uint32_t start = 0, end = len, k;
    while (start < end && numSpace(s[start])) start++;
    while (end > start && numSpace(s[end - 1])) end--;
    for (k = start + 1; k < end; k++) {
        if (!unitIsLetter(s[k])) continue;
        if (isFloat ? !parseFloatText(s + start, k - start, number) : !parseIntText(s + start, k - start, whole)) continue;
        if (unitOfText(s + k, end - k, unit)) return true;
    }
    return false;
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
    switch ((int)method) {
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
            double ignored;
            uint32_t unit;
            if (parseIntText(s, len, &whole)) return Int(whole);
            if (splitNumberUnit(s, len, false, &whole, &ignored, &unit)) return Int(whole, unit);
            if (method == 19) return Undef();
            return numberTextError(text, "' is not a whole number.");
        }
        case 18: case 20: {  /* ToFloat / TryToFloat */
            double number;
            int64_t ignored;
            uint32_t unit;
            if (parseFloatText(s, len, &number)) return Float((Real)number);
            if (splitNumberUnit(s, len, true, &ignored, &number, &unit)) return Float((Real)number, unit);
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
    switch ((int)method) {
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

/* ---- embedded files: `new Resource("path")` (docs/stdlib/resources.md) ------------------------------------------------------------------------------------------------- */
/* The files the compiler embedded in the program: the generated file points these at its table (a library without resources has none). */
typedef struct ResEntry { const char* name; const uint8_t* data; uint32_t length; } ResEntry;
extern FIRE_RT_DATA const ResEntry* g_resources;
extern FIRE_RT_DATA int g_resourceCount;

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
/* ---- `__EnvCall(id[, a[, b]])` (EnvironmentMethods): the process - arguments, path, environment variables, line break, platform. What depends on the
 * system is the platform layer's (fire_plat_eachArg, fire_plat_programPath, fire_plat_getenv/setenv/envEach, fire_plat_name). ---- */
extern FIRE_RT_DATA int g_argc;
extern FIRE_RT_DATA char** g_argv;
/* The entry point of a process hands over its arguments (before fire_init). */
FIRE_FN void fire_setArgs(int argc, char** argv) { g_argc = argc; g_argv = argv; }

typedef struct FireEnvPair { char* name; char* value; } FireEnvPair;
typedef struct FireEnvList { FireEnvPair* items; uint32_t count, cap; char** args; uint32_t argCount, argCap; } FireEnvList;
FIRE_FN void fire_envAddArg(const char* utf8, void* ctx) {
    FireEnvList* l = (FireEnvList*)ctx;
    size_t n = strlen(utf8);
    char* copy = (char*)malloc(n + 1);
    if (copy == NULL) return;
    memcpy(copy, utf8, n + 1);
    if (l->argCount == l->argCap) {
        uint32_t cap = l->argCap ? l->argCap * 2 : 8;
        char** grown = (char**)realloc(l->args, sizeof(char*) * cap);
        if (grown == NULL) { free(copy); return; }
        l->args = grown;
        l->argCap = cap;
    }
    l->args[l->argCount++] = copy;
}
FIRE_FN void fire_envAddVar(const char* name, size_t nameLen, const char* value, void* ctx) {
    FireEnvList* l = (FireEnvList*)ctx;
    size_t vn = strlen(value);
    FireEnvPair p;
    p.name = (char*)malloc(nameLen + 1);
    p.value = (char*)malloc(vn + 1);
    if (p.name == NULL || p.value == NULL) { free(p.name); free(p.value); return; }
    memcpy(p.name, name, nameLen); p.name[nameLen] = 0;
    memcpy(p.value, value, vn + 1);
    if (l->count == l->cap) {
        uint32_t cap = l->cap ? l->cap * 2 : 32;
        FireEnvPair* grown = (FireEnvPair*)realloc(l->items, sizeof(FireEnvPair) * cap);
        if (grown == NULL) { free(p.name); free(p.value); return; }
        l->items = grown;
        l->cap = cap;
    }
    l->items[l->count++] = p;
}
FIRE_FN int fire_envPairCmp(const void* a, const void* b) { return strcmp(((const FireEnvPair*)a)->name, ((const FireEnvPair*)b)->name); }

/* The name of a variable as UTF-8 (false: not a name - empty, or with '='). */
FIRE_FN bool fire_envName(Value v, char* out, uint32_t cap) {
    out[0] = 0;
    if (v.kind != K_String || strOf(v)->length == 0) return false;
    strToUtf8(v, out, cap);
    return out[0] != 0 && strchr(out, '=') == NULL;
}

FIRE_FN Value envCall(int64_t id, Value a, Value b, OwnList* list) {
    char name[1024];
    char text[1200];
    switch ((int)id) {
        case 1: {   /* Args */
            FireEnvList l;
            Arr* arr;
            uint32_t i;
            memset(&l, 0, sizeof l);
            fire_plat_eachArg(g_argc, g_argv, fire_envAddArg, &l);
            arr = allocArr(l.argCount, list);
            for (i = 0; i < l.argCount; i++) {
                arrItems(arr)[i] = newStrFromUtf8((const uint8_t*)l.args[i], strlen(l.args[i]), list);
                retain(arrItems(arr)[i]);
                free(l.args[i]);
            }
            free(l.args);
            return ArrV(arr);
        }
        case 2: {   /* ProgramPath: what the system tells, else argv[0] */
            char path[4096];
            int n = fire_plat_programPath(path, sizeof path);
            if (n <= 0 && g_argc > 0 && g_argv != NULL && g_argv[0] != NULL) { strncpy(path, g_argv[0], sizeof path - 1); path[sizeof path - 1] = 0; n = (int)strlen(path); }
            return newStrFromUtf8((const uint8_t*)path, n > 0 ? (size_t)n : 0, list);
        }
        case 3: {   /* Get */
            int n;
            char* value;
            Value r;
            if (!fire_envName(a, name, sizeof name)) { snprintf(text, sizeof text, "Environment.Get: '%s' is not a name of an environment variable.", name); return fireError("ArgumentException", text); }
            value = (char*)malloc(FIRE_ENV_VALUE_MAX);
            if (value == NULL) return Undef();
            n = fire_plat_getenv(name, value, FIRE_ENV_VALUE_MAX);
            r = n < 0 ? Undef() : newStrFromUtf8((const uint8_t*)value, (size_t)n, list);
            free(value);
            return r;
        }
        case 4: {   /* Set (undefined removes) */
            if (!fire_envName(a, name, sizeof name)) { snprintf(text, sizeof text, "Environment.Set: '%s' is not a name of an environment variable.", name); return fireError("ArgumentException", text); }
            if (b.kind == K_Undefined) { fire_plat_setenv(name, NULL); return Undef(); }
            if (b.kind != K_String) return fireError("ArgumentException", "Environment.Set: the value must be a string (or undefined to remove the variable).");
            {
                uint32_t cap = strOf(b)->length * 4 + 4;
                char* value = (char*)malloc(cap);
                if (value == NULL) return Undef();
                strToUtf8(b, value, cap);
                fire_plat_setenv(name, value);
                free(value);
            }
            return Undef();
        }
        case 5: {   /* Variables: name, value, name, value, ... sorted by name */
            FireEnvList l;
            Arr* arr;
            uint32_t i;
            memset(&l, 0, sizeof l);
            fire_plat_envEach(fire_envAddVar, &l);
            if (l.count > 1) qsort(l.items, l.count, sizeof(FireEnvPair), fire_envPairCmp);
            arr = allocArr(l.count * 2, list);
            for (i = 0; i < l.count; i++) {
                arrItems(arr)[2 * i] = newStrFromUtf8((const uint8_t*)l.items[i].name, strlen(l.items[i].name), list);
                arrItems(arr)[2 * i + 1] = newStrFromUtf8((const uint8_t*)l.items[i].value, strlen(l.items[i].value), list);
                retain(arrItems(arr)[2 * i]);
                retain(arrItems(arr)[2 * i + 1]);
                free(l.items[i].name);
                free(l.items[i].value);
            }
            free(l.items);
            return ArrV(arr);
        }
#if defined(_WIN32)
        case 6: return newStrFromUtf8((const uint8_t*)"\r\n", 2, list);
#else
        case 6: return newStrFromUtf8((const uint8_t*)"\n", 1, list);
#endif
        case 7: return newStrFromUtf8((const uint8_t*)fire_plat_name(), strlen(fire_plat_name()), list);
    }
    fatal("Unknown Environment function.");
    return Undef();
}

/* `__ResourceCall(op, id)`: 0 count, 1 length, 2 bytes (a buffer), 3 name, 4 text (UTF-8). */
FIRE_FN Value resourceCall(int64_t op, Value id, OwnList* list) {
    const ResEntry* r;
    if (op == 0) return Int(g_resourceCount);
    if (id.kind != K_Int) fatal("A Resource needs a path that is a string literal: new Resource(\"images/logo.png\") - the compiler embeds the file.");
    if (id.i < 0 || id.i >= g_resourceCount) fatal("No resource with that number.");
    r = &g_resources[id.i];
    switch ((int)op) {
        case 1: return Int((int64_t)r->length);
        case 2: { Buf* b = allocBuf(r->length, list); if (r->length) dataCopy(b->data, r->data, r->length); return BufV(b); }
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
        if (v.kind != K_Int && v.kind != K_Word) fatal("The format specifier expects an int.");
        u = v.kind == K_Word ? (uint64_t)v.w : (uint64_t)v.i;
        if (letter == 'X') {
            const char* hex = first == 'X' ? "0123456789ABCDEF" : "0123456789abcdef";
            do { digits[dn++] = hex[u & 15]; u >>= 4; } while (u);
        } else if (letter == 'B') {
            do { digits[dn++] = (char)('0' + (u & 1)); u >>= 1; } while (u);
        } else {
            negative = v.kind == K_Int && v.i < 0;
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
        if (v.kind != K_Int && v.kind != K_Float && v.kind != K_Word) fatal("The format specifier expects a number.");
        d = v.kind == K_Word ? (double)wordReal(v) : (double)toR(v);
        prec = hasNumber ? (int)number : (spec->length == 1 ? 2 : -1);
        if (prec < 0) fatal("Unknown format specifier.");
        if (isnan(d)) n = snprintf(text, sizeof text, "NaN");
        else if (isinf(d)) n = snprintf(text, sizeof text, "%s", d < 0 ? "-Infinity" : "Infinity");
        else n = snprintf(text, sizeof text, "%.*f", prec, FIRE_CC_FIXED(d, prec));
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
    const char* const* ifaces; uint32_t nIfaces;            /* every interface the class reaches (also over its base classes): Type.Interfaces */
    const char* const* declIfaces; uint32_t nDeclIfaces;    /* the ones it names itself: Type.DeclaredInterfaces */
    const RfMember* members; uint32_t nMembers;
    Value (*attrs)(OwnList* list);    /* the attributes before the class (generated), null: none */
    const char* const* stripped; uint32_t nStripped;   /* the methods stripping left out, after the highest level that keeps them ("1Bark"; docs/spec/NATIVE_BACKEND.md) */
    Value (*make)(int argc, const Value* items, OwnList* list, uint32_t caller);   /* a class of a plugin: Reflect.New (NULL: the program knows the class) */
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
/* An array of strings. */
FIRE_FN Value strArrayOf(const char* const* items, uint32_t n, OwnList* list) {
    Arr* a = allocArr(n, list);
    uint32_t i;
    for (i = 0; i < n; i++) { arrItems(a)[i] = strFromUtf8(items[i], list); retain(arrItems(a)[i]); }
    return ArrV(a);
}
/* Generated: builds a ReflectionException (message) owned by the global scope. */
FIRE_HOST_EXTERN Value makeReflectError(Value message);
#endif

FIRE_FN const char* kindName(Value v) {
    switch (v.kind) {
        case K_Bool: return "Bool"; case K_Int: return "Int"; case K_Float: return "Float"; case K_Char: return "Char"; case K_String: return "String";
        case K_Class: return "Class"; case K_Lambda: return "Lambda"; case K_PtrVar: case K_PtrByte: case K_PtrElem: case K_PtrField: return "Pointer"; case K_Array: return "Array"; case K_Buffer: return "Buffer";
        case K_Word: return "Word";
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
/* The hash of a key of Dictionary/HashSet (MathCall 15, MathMethods.KeyHash): numbers by their value in the base unit (an integral one like an int),
 * strings by their text, chars and bools by their value, everything else by identity. The tables keep the order of insertion: a different value than the
 * VM's changes nothing a program sees. */
FIRE_FN uint64_t fire_mixHash(uint64_t h) { h ^= h >> 33; h *= 0xff51afd7ed558ccdULL; h ^= h >> 33; return h; }
FIRE_FN int64_t keyHash(Value x) {
    switch (x.kind) {
        case K_Int: case K_Float: {
            double v, b;
            if (x.kind == K_Int && unitIsUnitless(x.unit)) return (int64_t)fire_mixHash((uint64_t)x.i);
            v = x.kind == K_Int ? (double)x.i : (double)x.f;
            b = v;
            if (!unitIsUnitless(x.unit) && !unitToBase(v, x.unit, &b)) b = v;
            if (b == floor(b) && fabs(b) < 9e15) return (int64_t)fire_mixHash((uint64_t)(int64_t)b);
            { uint64_t bits; memcpy(&bits, &b, sizeof bits); return (int64_t)fire_mixHash(bits); }
        }
        case K_String: {
            const Str* s = strOf(x);
            uint64_t h = 1469598103934665603ULL;
            uint32_t k;
            for (k = 0; k < s->length; k++) { h ^= (uint64_t)s->data[k]; h *= 1099511628211ULL; }
            return (int64_t)h;
        }
        case K_Undefined: return 0;
        case K_Word: return (int64_t)fire_mixHash((uint64_t)x.w);   /* (like an int with the same bits: `==` takes them as equal) */
        case K_Char: case K_Bool: return (int64_t)fire_mixHash((uint64_t)x.i ^ ((uint64_t)x.kind << 56));
        case K_Class: case K_Array: case K_Buffer: return (int64_t)fire_mixHash((uint64_t)leafKey(x));
        case K_PtrByte: case K_PtrElem: case K_PtrField: return (int64_t)fire_mixHash((uint64_t)(uintptr_t)ptrAddr(x));
        default: return (int64_t)fire_mixHash((uint64_t)(uintptr_t)x.p);
    }
}

FIRE_FN Value mathCall(int64_t method, Value x, Value y) {
    static const char* const names[] = {"", "Sqrt", "Round", "Floor", "Ceiling", "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Log", "Log10", "Exp"};
    const char* name = method >= 1 && method <= 14 ? names[method] : "?";
    char text[200];
    double v;
    if (method == 15) return Int(keyHash(x));
    if (x.kind != K_Int && x.kind != K_Float) {
        snprintf(text, sizeof text, "Math.%s expects a number, not a value of type '%s'.", name, kindName(x));
        return fireError("ArgumentException", text);
    }
    v = toR(x);
    switch ((int)method) {
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
    switch ((int)method) {
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

/* `x!Color` (OpCode.ToEnum): a whole number or a value of the same enum becomes a value of the enum `index`; `undefined` stays; anything else is an
 * ArgumentException (the text of the VM). */
FIRE_FN Value toEnum(Value v, uint16_t index) {
    if (v.kind == K_Int && (v.reserved == 0 || v.reserved == index)) return EnumV(v.i, index);
    if (v.kind != K_Undefined) {
        char text[200];
        const FireEnum* own = enumOf(v);
        snprintf(text, sizeof text, "Cannot convert a value of type '%s' to '%s'.", own ? own->name : kindName(v), index <= g_enumCount ? g_enums[index - 1].name : "?");
        fireError("ArgumentException", text);
    }
    return v;
}
/* `value.HasFlag(flag)`: all bits of `flag` are set in `value` (a flag of 0 is always set). */
FIRE_FN Value enumHasFlag(Value v, Value flag) { return Bool(v.kind == K_Int && flag.kind == K_Int && (v.i & flag.i) == flag.i); }

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
 * generator calls internTArgsV(2, &TA_1, &TA_2) (C89: no compound literals - Open Watcom). */
typedef struct fire_TArgsEntry { struct fire_TArgsEntry* next; uint32_t n; const TArg* a[1]; } fire_TArgsEntry;
extern FIRE_RT_DATA fire_TArgsEntry* g_targsHead;
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
#define FIRE_TARGS_MAX 16
FIRE_FN TArgs internTArgsV(uint32_t n, ...) {
    const TArg* items[FIRE_TARGS_MAX];
    va_list ap;
    uint32_t i;
    if (FIRE_UNLIKELY(n > FIRE_TARGS_MAX)) fatal("More than 16 type arguments.");
    va_start(ap, n);
    for (i = 0; i < n; i++) items[i] = va_arg(ap, const TArg*);
    va_end(ap);
    return internTArgsN(items, n);
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
    const int32_t* typeMap; /* a library module (ROADMAP 9): its type ids -> those of the program (fire_exct_M); NULL: the ids are the program's */
} HandlerInfo;
#define FIRE_HANDLER_TYPE(info, i) ((info)->typeMap && (info)->types[i] >= 0 ? (info)->typeMap[(info)->types[i]] : (info)->types[i])

struct Handler {
    Handler* prev;
    const HandlerInfo* info;
    void (*fn)(void* ctx, uint32_t catchIndex, Value exception);   /* runs the catch block (generated: a function that gets the frame of its function) */
    void* ctx;
    Handler* origin;      /* the handler this one stands for (a finally-only handler stands for the one that is running its catch) */
    uint8_t finallyOnly;
};

/* Does the exception match the catch type (`typeId` as stored in HandlerInfo.types; FIRE_EXC_ANY: `Exception`)? The descriptor of its class lists its types. */
FIRE_RT_FN bool excMatches(Value exception, int32_t typeId);
/* Generated: builds an IndexOutOfBoundsException (message, index, length) owned by the global scope. */
FIRE_HOST_EXTERN Value makeIndexError(Value message, int64_t index, int64_t length);
/* Generated: builds a UnitMismatchException (message, expected unit, actual unit) owned by the global scope. */
FIRE_HOST_EXTERN Value makeUnitError(Value message, Value expected, Value actual);

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

/* The index of the field `message` of an object of the class, -1 when the class has none (the descriptor of the class). */
FIRE_RT_FN int32_t excMessageIndex(uint32_t cls);
/* An exception that nothing catches: the text of the VM (UncaughtScriptException) - the class and the text of the field `message`. */
FIRE_COLD FIRE_FN void reportUnhandled(Value exception) {
    Obj* o = asObj(exception);
    int32_t index = excMessageIndex(o->cls);
    char text[1024];
    if (index < 0) snprintf(text, sizeof text, "(no 'message' field)");
    else {
        Value m = objFields(o)[index];
        if (m.kind != K_String) {
            OwnList scratch;
            ownListInit(&scratch, 0, NULL);
            m = toStringValue(m, &scratch);
        }
        strToUtf8(m, text, sizeof text);
    }
    fflush(stdout);
    fprintf(stderr, "Unhandled exception of type '%s': %s\n", className(o->cls), text);
}

#ifdef FIRE_THREADS
FIRE_FN Value threadUnhandled(Value exception);
FIRE_FN Value jobUnhandled(Value exception);
#endif

/* `throw exception`. Returns the resume value if the exception is resumed at this very point; otherwise it returns with `g_unwind.active` set and the caller
 * has to unwind (the generated code checks the flag after every call). `throwValue(x)` is resumable, `throwValue(x, false)` (a rethrow) is not. */
#ifdef FIRE_DEBUG
FIRE_RT_FN int fire_dbgUnhandled(Value exception);
FIRE_RT_FN void fire_dbgRet(DbgFrame* caller);
/* Would a catch block of this thread take the exception (VM.WouldBeCaught)? */
FIRE_FN bool wouldBeCaught(Value exception) {
    Handler* h;
    uint32_t i;
    for (h = g_handlers; h; h = h->prev)
        if (!h->finallyOnly)
            for (i = 0; i < h->info->ncatch; i++)
                if (h->info->types[i] < 0 || excMatches(exception, FIRE_HANDLER_TYPE(h->info, i))) return true;
    return false;
}
#endif
FIRE_FN Value throwValueR(Value exception, bool resumable) {
    Handler* h;
    if (FIRE_UNLIKELY(exception.kind != K_Class)) fatal("throw expects an exception object.");
    if (FIRE_UNLIKELY(!leafAlive(exception))) return destroyedError(exception);   /* (a destroyed object: what is thrown is the DestroyedException, like in the VM) */
    takeGlobal(asObj(exception));
#ifdef FIRE_DEBUG
    /* a debugger: an exception that nothing catches stops at the throw, before anything is unwound; `resume` ignores it (the throw gives undefined) */
    if (FIRE_UNLIKELY(g_dbgArmed) && !wouldBeCaught(exception) && fire_dbgUnhandled(exception)) return Undef();
#endif
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
            if (h->info->types[i] < 0 || excMatches(exception, FIRE_HANDLER_TYPE(h->info, i))) found = (int)i;
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
#ifdef FIRE_DEBUG
                if (FIRE_UNLIKELY(g_dbgArmed) && g_dbgTop) fire_dbgRet(g_dbgTop);   /* back at the throw site (like the VM after resume) */
#endif
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
        if (!p->cleared && sameRef(p->exception, exception)) {
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
        if (!p->cleared && sameRef(p->exception, exception)) { p->cleared = 1; return; }
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
    fatalException("IndexOutOfBoundsException", text);
    FIRE_NOT_REACHED(Undef())
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
    char expected[120], text[300];
    uint32_t actualUnit = (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) ? v.unit : 0;
    strToUtf8(expectedText, expected, sizeof expected);
    snprintf(text, sizeof text, "Expected unit '%s', got: %s.", expected, actualUnit ? g_ud[actualUnit].name : "(no unit)");
    fatalException("UnitMismatchException", text);
#endif
}

#ifndef FIRE_LIBRARY
#ifdef FIRE_EXCEPTIONS
/* Generated: the exception object of the class `cls` (a class that natives of packages throw: `exceptions` of the package) with the message, or an undefined
 * value. */
FIRE_HOST_EXTERN Value makeClassError(const char* cls, Value message);
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
#endif
    fatalException(cls, utf8);
    FIRE_NOT_REACHED(Undef())
}
#endif

/* The kind of a value as the VM names it (in the texts of the errors of calls and fields). */
FIRE_FN const char* valueKindText(Value v) { return kindName(v); }

/* A method that the value does not have (a class that has it with other numbers of arguments is reported by the dispatcher itself): a MissingMemberException. */
FIRE_COLD FIRE_FN Value missingMethod(Value self, const char* name, int argc) {
    char text[400];
    if (self.kind == K_Class && !leafAlive(self)) return destroyedError(self);   /* (the VM looks at a destroyed object first) */
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
    fatalException("ReflectionException", utf8);
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
extern FIRE_RT_DATA ProbeNode* g_probeNodes;
extern FIRE_RT_DATA int64_t g_probeNextId;

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
#ifdef FIRE_MODULES
#define FIRE_PROBE_RAW() (raw ? raw(obj, value, &local, caller) : fireProbeRawSet(obj, name, value, &local, caller))
#else
#define FIRE_PROBE_RAW() raw(obj, value, &local, caller)
#endif
    if (!node || nested || node->nrunning >= 8) { FIRE_PROBE_RAW(); leave(&local); return; }
    node->running[node->nrunning++] = name;
#ifdef FIRE_MODULES
    oldValue = get ? get(obj, &local) : fireProbeRawGet(obj, name, &local);
#else
    oldValue = get(obj, &local);
#endif
    retain(oldValue);   /* (the assignment below may drop the last reference of the old value: the probes still compare with it and get it) */
    for (i = 0; i < node->n && !g_unwind.active; i++) {
        ProbeEntry e = node->entries[i];
        Value verdict;
        if (!e.changing || (e.member && strcmp(e.member, name) != 0)) continue;
        verdict = probeRun(&e, obj, name, oldValue, value, &local);
        if (g_unwind.active) break;
        if (verdict.kind == K_Bool && !verdict.i) { cancelled = true; break; }
    }
    if (!g_unwind.active && !cancelled) {
        FIRE_PROBE_RAW();
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
    release(oldValue);
    leave(&local);
#undef FIRE_PROBE_RAW
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
FIRE_HOST_EXTERN Value makeAccessError(Value message);

/* A private or protected member used from where it may not be (SPEC 5.7): an AccessDeniedException, or the end of the program when there are no exceptions. */
FIRE_FN Value accessDenied(const char* text) {
#ifdef FIRE_EXCEPTIONS
    return throwValue(makeAccessError(fire_asciiMessage(text)));
#else
    fatalException("AccessDeniedException", text);
#endif
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Time in the runtime: ticks of 100 ns like in the VM; used by `Sleep` and the other time bridge functions, by the waiting functions of the devices and by
 * `#timeout`.
 * --------------------------------------------------------------------------------------------------------------------- */
#define TICKS_PER_MS ((int64_t)10000)

/* The `ticks` of a TimeSpan (any class with a whole number field `ticks`, the descriptor of the class); false for another object (and for every object when the
 * program has none). */
FIRE_RT_FN bool timeObjTicks(Value v, int64_t* ticks);

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
extern FIRE_RT_DATA int64_t g_defaultTimeoutTicks;

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
FIRE_HOST_EXTERN Value makeDestroyedError(Value message);

/* Generated: builds a DivideByZeroException (message) owned by the global scope. */
FIRE_HOST_EXTERN Value makeDivideError(Value message);

FIRE_FN Value divideByZero(void) {
#ifdef FIRE_EXCEPTIONS
    return throwValue(makeDivideError(fire_asciiMessage("Division by zero.")));
#elif defined(FIRE_LIBRARY)
    return fireError("DivideByZeroException", "Division by zero.");
#else
    fatalException("DivideByZeroException", "Division by zero.");
    FIRE_NOT_REACHED(Undef())
#endif
}

/* The text of the DestroyedException for a reference from before a `fire handover` (the value lives on in the thread), else `text`. */
FIRE_COLD FIRE_FN const char* handedOverText(Value leaf, const char* text) {
    if (!handedOverRef(leaf)) return text;
    return leaf.kind == K_Buffer ? "Access to a buffer that was handed over to a thread ('fire handover')."
         : leaf.kind == K_Class ? "Access to an object that was handed over to a thread ('fire handover')."
         : "Access to an array that was handed over to a thread ('fire handover').";
}
FIRE_FN Value destroyedError(Value leaf) {
    const char* text = leaf.kind == K_Buffer ? "Access to a destroyed buffer." : leaf.kind == K_Class ? "Access to a destroyed object." : "Access to a destroyed array.";
#ifdef FIRE_THREADS
    text = handedOverText(leaf, text);   /* (only a program with threads can hand over: without them destroyedError needs nothing but the kind) */
#endif
#ifdef FIRE_EXCEPTIONS
    return throwValue(makeDestroyedError(fire_asciiMessage(text)));
#else
    fatalException("DestroyedException", text);
    FIRE_NOT_REACHED(Undef())
#endif
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Arrays and byte buffers
 * --------------------------------------------------------------------------------------------------------------------- */

/* `a[i]` for arrays, buffers and strings (objects with a GetIndex method are handled by the generated code). */
/* ---- Typed arrays (SPEC 8.4.2, ArrTyped above; the VM: Values.ArrayElem) ------------------------------------------------------------------ */
FIRE_FN const char* arrElemName(uint8_t e) {
    switch (e) {
        case E_I8: return "int[8]"; case E_I16: return "int[16]"; case E_I32: return "int[32]"; case E_I64: return "int";
        case E_F8: return "float[8]"; case E_F16: return "float[16]"; case E_F32: return "float[32]"; case E_F64: return "float";
        case E_Bool: return "bool"; case E_Char: return "char"; case E_U8: return "byte"; default: return "any";
    }
}
FIRE_FN bool arrElemIsFloat(uint8_t e) { return e >= E_F8 && e <= E_F64; }
FIRE_FN int arrElemBits(uint8_t e) { return e == E_I8 || e == E_F8 || e == E_U8 ? 8 : e == E_I16 || e == E_F16 ? 16 : e == E_I32 || e == E_F32 ? 32 : 64; }
/* The bytes of one packed element. */
FIRE_FN size_t arrElemSize(uint8_t e) {
    switch (e) {
        case E_I8: case E_U8: case E_Bool: return 1;
        case E_I16: case E_Char: return 2;
        case E_I32: case E_F8: case E_F16: case E_F32: return 4;
        case E_F64: return sizeof(Real);
        case E_I64: return sizeof(FireInt);   /* (`int`: 4 bytes with #intwidth 32) */
        default: return 8;
    }
}
/* The hints of `undefined` for the float elements: NaNs with a payload nothing computes. */
#define FIRE_UNDEF_F32 0x7FC0F1E5u
#define FIRE_UNDEF_F64 0x7FF8F1E5F1E5F1E5ull

/* The value as an element of the typed array `t` (VM: ArrayElem.TryConvert): an int cut to the width, a float rounded, an int becomes a float in a float
 * array, a number converted into the unit of the elements; `undefined` fits every type. Anything else is the exception (ArgumentException,
 * UnitMismatchException, OverflowException) - then *ok is false. */
FIRE_COLD FIRE_FN Value arrElemWrong(const ArrTyped* t, Value v, bool* ok) {
    char text[200];
    snprintf(text, sizeof text, "An element of an array of %s must be %s (or undefined), not %s.", arrElemName(t->elem),
             t->elem == E_Bool ? "a bool" : t->elem == E_Char ? "a char" : arrElemIsFloat(t->elem) ? "a number" : "an int", coerceKindName(v.kind));
    fireError("ArgumentException", text);
    *ok = false;
    return Undef();
}
FIRE_FN Value arrElemConvert(const ArrTyped* t, Value v, bool* ok) {
    bool isFloat;
    *ok = true;
    if (t->elem == E_Bool || t->elem == E_Char) {
        if (v.kind == K_Undefined) return Undef();
        if (v.kind == (t->elem == E_Bool ? K_Bool : K_Char)) return v;
    } else {
        isFloat = arrElemIsFloat(t->elem);
        /* (the common case - an int without a unit into an array of ints without one: the width only) */
        if (FIRE_LIKELY(v.kind == K_Int && !isFloat && v.unit == 0 && !(t->flags & (TU_ANY | TU_UNIT)))) { v.reserved = 0; return intToWidth(v, arrElemBits(t->elem), t->elem == E_U8); }
        if (v.kind == K_Int || v.kind == K_Undefined || (isFloat && v.kind == K_Float)) {
            if (t->flags & TU_ANY) {
                /* (`: any`: every unit, kept) */
            } else if (t->flags & TU_UNIT) {
                v = adaptUnit(v, t->unit, (t->flags & TU_DIMONLY) != 0, false, t->unitText);
                if (g_unwind.active) { *ok = false; return Undef(); }
            } else if (v.unit != 0 && !unitIsUnitless(v.unit)) {
                unitMismatch(v, t->unitText);
                *ok = false;
                return Undef();
            }
            if (v.kind == K_Undefined) return v;
            return convertWidth(v, arrElemBits(t->elem), isFloat, t->elem == E_U8);
        }
    }
    return arrElemWrong(t, v, ok);
}

FIRE_FN bool arrIsReal(const ArrTyped* t, uint32_t i) { return t->real && (t->real[i >> 3] >> (i & 7) & 1); }
FIRE_FN void arrSetReal(ArrTyped* t, uint32_t i, uint32_t length, bool real) {
    if (!t->real) {
        if (!real) return;
        t->real = (uint8_t*)calloc((length + 7) / 8, 1);
        if (FIRE_UNLIKELY(!t->real)) allocFailed();
    }
    if (real) t->real[i >> 3] |= (uint8_t)(1u << (i & 7)); else t->real[i >> 3] &= (uint8_t)~(1u << (i & 7));
}

/* Element i of a packed array of the element `elem` (the generated code gives a constant: the switch folds - see arrPackedGet). */
FIRE_ALWAYS_INLINE FIRE_FN Value arrPackedAt(const Arr* a, uint32_t i, uint8_t elem) {
    const ArrTyped* t = a->typed;
    uint32_t u = (t->flags & TU_UNIT) ? t->unit : 0;
    switch (elem) {
        case E_I8: { int8_t x = ((const int8_t*)t->data)[i]; if (FIRE_LIKELY(x != INT8_MIN) || arrIsReal(t, i)) return Int(x, u); break; }
        case E_I16: { int16_t x = ((const int16_t*)t->data)[i]; if (FIRE_LIKELY(x != INT16_MIN) || arrIsReal(t, i)) return Int(x, u); break; }
        case E_I32: { int32_t x = ((const int32_t*)t->data)[i]; if (FIRE_LIKELY(x != INT32_MIN) || arrIsReal(t, i)) return Int(x, u); break; }
        case E_I64: { FireInt x = ((const FireInt*)t->data)[i]; if (FIRE_LIKELY(x != FIRE_INT_UNDEF) || arrIsReal(t, i)) return Int(x, u); break; }
        case E_U8: { uint8_t x = ((const uint8_t*)t->data)[i]; if (FIRE_LIKELY(x != 0xFF) || arrIsReal(t, i)) return Int(x, u); break; }
        case E_F8: case E_F16: case E_F32: {
            float x = ((const float*)t->data)[i];
            uint32_t bits;
            memcpy(&bits, &x, sizeof bits);
            if (FIRE_LIKELY(bits != FIRE_UNDEF_F32) || arrIsReal(t, i)) return Float((Real)x, u);
            break;
        }
        case E_F64: {
            Real x = ((const Real*)t->data)[i];
#ifdef FIRE_FLOAT32
            uint32_t bits; const uint32_t hint = FIRE_UNDEF_F32;
#else
            uint64_t bits; const uint64_t hint = FIRE_UNDEF_F64;
#endif
            memcpy(&bits, &x, sizeof bits);
            if (FIRE_LIKELY(bits != hint) || arrIsReal(t, i)) return Float(x, u);
            break;
        }
        case E_Bool: { uint8_t x = ((const uint8_t*)t->data)[i]; if (FIRE_LIKELY(x != 2)) return Bool(x != 0); break; }
        case E_Char: { uint16_t x = ((const uint16_t*)t->data)[i]; if (FIRE_LIKELY(x != 0xFFFF) || arrIsReal(t, i)) return Char(x); break; }
    }
    return UndefU(u);   /* (`undefined` of an array with a unit carries it, like in the VM: it prints as `undefined:mm`) */
}
/* Element i of a packed array. */
FIRE_FN Value arrPackedGet(const Arr* a, uint32_t i) { return arrPackedAt(a, i, a->typed->elem); }

/* Element i of a packed array = `v` (converted already, see arrElemConvert). */
FIRE_FN void arrPackedSet(Arr* a, uint32_t i, Value v) {
    ArrTyped* t = a->typed;
    bool undef = v.kind == K_Undefined, hint = false;
    switch (t->elem) {
        case E_I8: { int8_t x = undef ? INT8_MIN : (int8_t)v.i; hint = !undef && x == INT8_MIN; ((int8_t*)t->data)[i] = x; break; }
        case E_I16: { int16_t x = undef ? INT16_MIN : (int16_t)v.i; hint = !undef && x == INT16_MIN; ((int16_t*)t->data)[i] = x; break; }
        case E_I32: { int32_t x = undef ? INT32_MIN : (int32_t)v.i; hint = !undef && x == INT32_MIN; ((int32_t*)t->data)[i] = x; break; }
        case E_I64: { FireInt x = undef ? FIRE_INT_UNDEF : (FireInt)v.i; hint = !undef && x == FIRE_INT_UNDEF; ((FireInt*)t->data)[i] = x; break; }
        case E_U8: { uint8_t x = undef ? 0xFF : (uint8_t)v.i; hint = !undef && x == 0xFF; ((uint8_t*)t->data)[i] = x; break; }
        case E_F8: case E_F16: case E_F32: {
            float x;
            uint32_t bits = FIRE_UNDEF_F32;
            if (undef) memcpy(&x, &bits, sizeof x); else { x = (float)v.f; memcpy(&bits, &x, sizeof bits); hint = bits == FIRE_UNDEF_F32; }
            ((float*)t->data)[i] = x;
            break;
        }
        case E_F64: {
            Real x;
#ifdef FIRE_FLOAT32
            uint32_t bits = FIRE_UNDEF_F32; const uint32_t hintBits = FIRE_UNDEF_F32;
#else
            uint64_t bits = FIRE_UNDEF_F64; const uint64_t hintBits = FIRE_UNDEF_F64;
#endif
            if (undef) memcpy(&x, &bits, sizeof x); else { x = v.f; memcpy(&bits, &x, sizeof bits); hint = bits == hintBits; }
            ((Real*)t->data)[i] = x;
            break;
        }
        case E_Bool: ((uint8_t*)t->data)[i] = undef ? 2 : (v.i ? 1 : 0); return;
        case E_Char: { uint16_t x = undef ? 0xFFFF : (uint16_t)v.i; hint = !undef && x == 0xFFFF; ((uint16_t*)t->data)[i] = x; break; }
    }
    if (hint || t->real) arrSetReal(t, i, a->length, hint);
}

/* The Values of a packed array (a native function asks for them): unpacked once; the type stays (arraySet still converts). */
FIRE_COLD FIRE_FN Value* arrUnpack(Arr* a) {
    ArrTyped* t = a->typed;
    Value* items = (t->own & AT_INLINE) ? (Value*)((char*)a + FIRE_HDR(Arr)) : (Value*)dataAlloc(1, a->length, sizeof(Value));
    uint32_t i;
    if (FIRE_UNLIKELY(!items)) allocFailed();
    for (i = 0; i < a->length; i++) items[i] = arrPackedGet(a, i);
    if (t->own & AT_DATA) dataFree(t->data);
    free(t->real);
    t->real = NULL;
    t->data = items;
    t->packed = 0;
    t->own = (uint8_t)((t->own & ~AT_DATA) | ((t->own & AT_INLINE) ? 0 : AT_DATA));
    return items;
}

/* Element i of an array (in range, alive): a Value of an untyped or unpacked one, else the packed number. */
FIRE_FN Value arrElemGet(Arr* a, uint32_t i) {
    if (FIRE_LIKELY(!a->typed)) return ((Value*)((char*)a + FIRE_HDR(Arr)))[i];
    return a->typed->packed ? arrPackedGet(a, i) : ((Value*)a->typed->data)[i];
}

FIRE_FN ArrTyped* arrTypedNew(uint8_t elem, uint32_t unit, uint8_t flags, Value unitText) {
    ArrTyped* t = (ArrTyped*)calloc(1, sizeof(ArrTyped));
    if (FIRE_UNLIKELY(!t)) allocFailed();
    t->elem = elem; t->unit = unit; t->flags = flags; t->unitText = unitText;
    t->own = AT_SELF;
    return t;
}

/* Makes the untyped array `a` typed: its elements are converted (false: one did not fit, the exception is thrown and `a` stays as it was). An array with a
 * unit demand of its own dimension only (`: any m`) keeps its Values (each keeps its unit); every other one is packed. */
FIRE_FN bool arrMakeTyped(Arr* a, uint8_t elem, uint32_t unit, uint8_t flags, Value unitText) {
    ArrTyped probe;
    ArrTyped* t;
    Value* items = (Value*)((char*)a + FIRE_HDR(Arr));
    uint32_t i;
    bool ok;
    memset(&probe, 0, sizeof probe);
    probe.elem = elem; probe.unit = unit; probe.flags = flags; probe.unitText = unitText;
    /* (`undefined` without a unit - a new array is full of it - fits every typed array without a unit demand as it is) */
#define FIRE_ARR_PLAIN_UNDEF(x) ((x).kind == K_Undefined && (x).unit == 0 && !(flags & TU_UNIT))
    for (i = 0; i < a->length; i++) { if (FIRE_ARR_PLAIN_UNDEF(items[i])) continue; arrElemConvert(&probe, items[i], &ok); if (!ok) return false; }
    t = arrTypedNew(elem, unit, flags, unitText);
    t->own |= AT_INLINE;
    a->typed = t;
    if (flags & (TU_DIMONLY | TU_ANY)) {
        for (i = 0; i < a->length; i++) items[i] = arrElemConvert(t, items[i], &ok);   /* (numbers: nothing to count) */
        t->data = items;
        return true;
    }
    t->data = dataAlloc(1, a->length, arrElemSize(elem));
    if (FIRE_UNLIKELY(!t->data)) allocFailed();
    t->own |= AT_DATA;
    t->packed = 1;
    for (i = 0; i < a->length; i++) {
        Value v;
        if (FIRE_ARR_PLAIN_UNDEF(items[i])) { arrPackedSet(a, i, items[i]); continue; }
        v = arrElemConvert(t, items[i], &ok);
        release(items[i]);
        arrPackedSet(a, i, v);
    }
#undef FIRE_ARR_PLAIN_UNDEF
    return true;
}

/* OpCode.TypeArray: the array `v` (at depth `rank` its inner arrays) goes into a declaration of a typed array. False: the exception was thrown. */
FIRE_COLD FIRE_FN bool typeArrayWrong(Value v, uint8_t elem) {
    char text[160];
    snprintf(text, sizeof text, "An array of %s is declared here, not %s.", arrElemName(elem), coerceKindName(v.kind));
    fireError("ArgumentException", text);
    return false;
}
FIRE_FN bool typeArraySlow(Value v, uint8_t elem, int rank, uint32_t unit, uint8_t flags, Value unitText, int depth);
/* (the common case - an array that has this type already, `int b[] = this.buckets` - embedded: one look at its type) */
FIRE_ALWAYS_INLINE FIRE_FN bool typeArray(Value v, uint8_t elem, int rank, uint32_t unit, uint8_t flags, Value unitText, int depth) {
    if (FIRE_LIKELY(rank == 1 && v.kind == K_Array && leafAlive(v) && arrOf(v)->typed && arrOf(v)->typed->elem == elem && arrOf(v)->typed->flags == flags
                    && (!(flags & TU_UNIT) || arrOf(v)->typed->unit == unit))) return true;
    return typeArraySlow(v, elem, rank, unit, flags, unitText, depth);
}
FIRE_FN bool typeArraySlow(Value v, uint8_t elem, int rank, uint32_t unit, uint8_t flags, Value unitText, int depth) {
    Arr* a;
    char text[160];
    if (v.kind == K_Undefined) return true;
    /* (a declaration of a typed array holds an array or nothing - like `int x` holds an int) */
    if (v.kind != K_Array) return typeArrayWrong(v, elem);
    if (!leafAlive(v) || depth > 64) return true;
    a = arrOf(v);
    if (depth < rank) {
        uint32_t i;
        Value* items = arrValues(a);
        if (!items) return true;
        for (i = 0; i < a->length; i++) if (!typeArray(items[i], elem, rank, unit, flags, unitText, depth + 1)) return false;
        return true;
    }
    if (a->typed) {
        if (a->typed->elem == elem && a->typed->flags == flags && (!(flags & TU_UNIT) || a->typed->unit == unit)) return true;
        snprintf(text, sizeof text, "An array of %s cannot be stored where an array of %s is declared.", arrElemName(a->typed->elem), arrElemName(elem));
        fireError("ArgumentException", text);
        return false;
    }
    return arrMakeTyped(a, elem, unit, flags, unitText);
}

/* A `byte` declaration holds a byte buffer (SPEC 8.4.2; the VM: BufferFromArrayValue): the array `v` stored there is replaced by a buffer with its elements -
 * THE conversion of an array into a buffer (an int is cut to 8 bits like `buffer[i] = 300`, `undefined` is 0, anything else an ArgumentException: then false
 * and *out stays). The buffer gets the owner of the array (an inner one's: the outer array), `list` when the array has none (it travels). At depth `rank` the
 * inner arrays are converted. A buffer and every other value stay as they are. */
FIRE_FN bool bufFromArray(Value v, int rank, int depth, OwnList* list, Value* out, bool words) {
    Arr* a;
    Buf* b;
    uint32_t i;
    char text[120];
    *out = v;
    if (v.kind != K_Array || !leafAlive(v) || depth > 64) return true;
    a = arrOf(v);
    if (depth < rank) {
        Value* items = arrValues(a);
        if (!items) return true;
        for (i = 0; i < a->length; i++) { Value r; if (!bufFromArray(items[i], rank, depth + 1, a->parts ? a->parts : list, &r, words)) return false; items[i] = r; }
        return true;
    }
    for (i = 0; i < a->length; i++) {
        Value e = arrElemGet(a, i);
        if (e.kind != K_Int && e.kind != K_Undefined && !(words && e.kind == K_Word)) {
            if (words) snprintf(text, sizeof text, "An element of a word buffer must be an int or a word, not %s.", coerceKindName(e.kind));
            else snprintf(text, sizeof text, "An element of a byte buffer must be an int, not %s.", coerceKindName(e.kind));
            fireError("ArgumentException", text);
            return false;
        }
    }
    if (words) {
        /* a buffer of machine words (SPEC 8.10; the VM: BufferFromArrayValue): an int or a word cut to the width, `undefined` 0 */
        b = allocBufOf(a->length, (uint8_t)sizeof(FireWord), a->h.owner ? a->h.owner : list);
        for (i = 0; i < a->length; i++) { Value e = arrElemGet(a, i); bufWords(b)[i] = e.kind == K_Int ? (FireWord)(uint64_t)(int64_t)e.i : e.kind == K_Word ? (FireWord)e.w : 0; }
        *out = BufV(b);
        return true;
    }
    b = allocBuf(a->length, a->h.owner ? a->h.owner : list);
    for (i = 0; i < a->length; i++) { Value e = arrElemGet(a, i); b->data[i] = e.kind == K_Int ? (uint8_t)e.i : 0; }
    *out = BufV(b);
    return true;
}
/* OpCode.TypeArray of a `byte` declaration: the value converted (bufFromArray); of an exception the value as it was (the generated code looks at the
 * unwinding flag). */
FIRE_FN Value typeBuffer(Value v, int rank, OwnList* list) {
    Value r;
    if (!bufFromArray(v, rank, 1, list, &r, false)) return v;
    return r;
}
/* The same for a `word` declaration: a buffer of machine words (SPEC 8.10). */
FIRE_FN Value typeWordBuffer(Value v, int rank, OwnList* list) {
    Value r;
    if (!bufFromArray(v, rank, 1, list, &r, true)) return v;
    return r;
}
/* `word a[] = new int[n]`, `new word[n]`, `word a[n]` (NewArray right before TypeArray): n zero words at once. */
FIRE_FN Value newWordBuffer(Value size, OwnList* list) {
    if (size.kind != K_Int || size.i < 0 || size.i > 0x7FFFFFFF) fatal("A buffer size must be a non-negative int.");
    return BufV(allocBufOf((uint32_t)size.i, (uint8_t)sizeof(FireWord), list));
}

/* `int[8] a[] = new int[n]` (NewArray right before TypeArray): the packed array made at once - header, ArrTyped and the numbers in one block. */
FIRE_FN Value newTypedArray(Value size, uint8_t elem, uint32_t unit, uint8_t flags, Value unitText, OwnList* list) {
    size_t head = FIRE_HDR(Arr) + ((sizeof(ArrTyped) + 7u) & ~(size_t)7u);
    uint32_t length, i;
    Arr* a;
    ArrTyped* t;
    if (size.kind != K_Int || size.i < 0 || size.i > 0x7FFFFFFF) fatal("An array size must be a non-negative int.");
    if (flags & (TU_DIMONLY | TU_ANY)) {   /* (keeps Values, see arrMakeTyped) */
        Value r = ArrV(allocArr((uint32_t)size.i, list));
        typeArray(r, elem, 1, unit, flags, unitText, 1);
        return r;
    }
    length = (uint32_t)size.i;
    a = (Arr*)dataAlloc(head + 1, length, arrElemSize(elem));
    if (FIRE_UNLIKELY(!a)) allocFailed();
    a->h.okind = O_Array;
    a->h.flags = 0;
    a->length = length;
    a->parts = NULL;
    t = (ArrTyped*)((char*)a + FIRE_HDR(Arr));
    memset(t, 0, sizeof *t);
    t->elem = elem; t->unit = unit; t->flags = flags; t->unitText = unitText;
    t->data = (char*)a + head;
    t->packed = 1;
    a->typed = t;
    for (i = 0; i < length; i++) arrPackedSet(a, i, Undef());
    slotAcquire(&a->h);
    ownLink(list, &a->h);
    return ArrV(a);
}

/* A copy of a packed array (copy, taking, sync) made at once like newTypedArray - the numbers only, no Values beside them (on 16-bit DOS those would not fit).
 * NULL when `src` is not packed (the caller then makes an array of Values). */
FIRE_FN Arr* packedCopy(const Arr* src, OwnList* list) {
    const ArrTyped* s = src->typed;
    size_t head = FIRE_HDR(Arr) + ((sizeof(ArrTyped) + 7u) & ~(size_t)7u);
    Arr* a;
    ArrTyped* t;
    if (!s || !s->packed) return NULL;
    a = (Arr*)dataAlloc(head + 1, src->length, arrElemSize(s->elem));
    if (FIRE_UNLIKELY(!a)) allocFailed();
    a->h.okind = O_Array;
    a->h.flags = 0;
    a->length = src->length;
    a->parts = NULL;
    t = (ArrTyped*)((char*)a + FIRE_HDR(Arr));
    memset(t, 0, sizeof *t);
    t->elem = s->elem; t->unit = s->unit; t->flags = s->flags; t->unitText = s->unitText;
    t->data = (char*)a + head;
    t->packed = 1;
    a->typed = t;
    dataCopy(t->data, s->data, (uint32_t)((uint64_t)src->length * arrElemSize(s->elem)));
    if (s->real) {
        t->real = (uint8_t*)malloc((src->length + 7) / 8);
        if (FIRE_UNLIKELY(!t->real)) allocFailed();
        memcpy(t->real, s->real, (src->length + 7) / 8);
    }
    slotAcquire(&a->h);
    ownLink(list, &a->h);
    return a;
}

/* A copy of the typed array `src` (copy, taking, sync): `dst` (new, untyped, `undefined` elements) gets its type; a packed one its numbers too (true: the
 * caller copies no elements). */
FIRE_FN bool arrCopyTyped(Arr* dst, const Arr* src) {
    const ArrTyped* s = src->typed;
    ArrTyped* t;
    if (!s) return false;
    t = arrTypedNew(s->elem, s->unit, s->flags, s->unitText);
    t->own |= AT_INLINE;
    dst->typed = t;
    if (!s->packed) { t->data = (char*)dst + FIRE_HDR(Arr); return false; }
    t->data = dataAlloc(1, src->length, arrElemSize(s->elem));
    if (FIRE_UNLIKELY(!t->data)) allocFailed();
    dataCopy(t->data, s->data, (uint32_t)((uint64_t)src->length * arrElemSize(s->elem)));
    t->own |= AT_DATA;
    t->packed = 1;
    if (s->real) {
        t->real = (uint8_t*)malloc((src->length + 7) / 8);
        if (FIRE_UNLIKELY(!t->real)) allocFailed();
        memcpy(t->real, s->real, (src->length + 7) / 8);
    }
    return true;
}

FIRE_FN Value arrayGet(Value a, Value i) {
    char text[160];
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        Arr* arr;
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        arr = arrOf(a);
        if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) return indexError("Array index", i.i, arr->length);
        return arrElemGet(arr, (uint32_t)i.i);
    }
    if (i.kind == K_Word && (a.kind == K_Array || a.kind == K_Buffer || a.kind == K_String)) return arrayGet(a, Int((int64_t)(uint64_t)i.w));   /* (a word as an index: its bits as an int) */
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        Buf* b;
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        b = bufOf(a);
        if ((uint64_t)i.i >= b->length) return indexError("Array index", i.i, b->length);
        if (FIRE_UNLIKELY(b->elem != 1)) return WordV((uint64_t)bufWords(b)[i.i]);   /* (a buffer of machine words) */
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

/* `a[i] = v`; the array holds a count of the new element and lets go of the old one. The result is the value stored: of a typed array the converted one
 * (`a[0] = 300` in an int[8] array is 44), which is also what the assignment yields. */
FIRE_FN Value arraySet(Value a, Value i, Value v) {
    char text[160];
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        Arr* arr;
        Value old;
        Value* items;
        if (FIRE_UNLIKELY(!leafAlive(a))) { destroyedError(a); return v; }
        arr = arrOf(a);
        if (FIRE_UNLIKELY(arr->typed != NULL)) {
            /* a typed array: the value is converted, or it is the exception - before the index is checked, like in the VM */
            bool ok;
            v = arrElemConvert(arr->typed, v, &ok);
            if (!ok) return v;
            if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) { indexError("Array index", i.i, arr->length); return v; }
            if (arr->typed->packed) { arrPackedSet(arr, (uint32_t)i.i, v); return v; }
        } else if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) { indexError("Array index", i.i, arr->length); return v; }
        items = arrValues(arr);
        old = items[i.i];
        items[i.i] = v;
        retain(v);
        release(old);
        return v;
    }
    if (i.kind == K_Word && (a.kind == K_Array || a.kind == K_Buffer || a.kind == K_String)) return arraySet(a, Int((int64_t)(uint64_t)i.w), v);   /* (a word as an index: its bits as an int) */
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        Buf* b;
        if (FIRE_UNLIKELY(!leafAlive(a))) { destroyedError(a); return v; }
        b = bufOf(a);
        if (FIRE_UNLIKELY(b->elem != 1)) {
            /* a buffer of machine words (SPEC 8.10): an int or a word, cut to the width */
            Value stored;
            if (v.kind != K_Int && v.kind != K_Word) {
                snprintf(text, sizeof text, "An element of a word buffer must be an int or a word, not %s.", coerceKindName(v.kind));   /* (the same text in the VM) */
                fireError("ArgumentException", text);
                return v;
            }
            if ((uint64_t)i.i >= b->length) { indexError("Array index", i.i, b->length); return v; }
            stored = wordOf(v, true);
            bufWords(b)[i.i] = (FireWord)stored.w;
            return stored;
        }
        if (v.kind == K_Word) v = Int((int64_t)(uint64_t)v.w);   /* (a word into a byte buffer: cut like an int) */
        if (v.kind != K_Int) {
            snprintf(text, sizeof text, "An element of a byte buffer must be an int, not %s.", coerceKindName(v.kind));   /* (the same text in the VM) */
            fireError("ArgumentException", text);
            return v;
        }
        if ((uint64_t)i.i >= b->length) { indexError("Array index", i.i, b->length); return v; }
        b->data[i.i] = (uint8_t)v.i;
        return v;
    }
    if (a.kind == K_String) { fireError("ArgumentException", "Strings are immutable - 's[i] = ...' is not possible (Replace/Substring return a new string)."); return v; }
    snprintf(text, sizeof text, "Index assignment ('[]=') on a value of type %s is not possible (neither an array nor a class with a 'SetIndex' method).", coerceKindName(a.kind));
    fireError("ArgumentException", text);
    return v;
}

/* ---------------------------------------------------------------------------------------------------------------------
 * `ref` parameters (SPEC 5.4.2): the argument is a pointer to the variable, the field or the element of the caller.
 * A pointer has one of three kinds: K_PtrVar a variable or a field (`unit` = the offset in elements: such a pointer is an "array" with one element), K_PtrByte
 * an element of a byte buffer, K_PtrElem an element of an array (both carry the handle of the buffer or array: see leafPtrV).
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN Value PtrV(Value* p) { Value r; r.kind = K_PtrVar; FIRE_PAD0(r) r.reserved = 0; r.unit = 0; r.p = p; return r; }
FIRE_FN Value BytePtrV(uint8_t* p, const Owned* buf) { return leafPtrV(K_PtrByte, p, buf); }
FIRE_FN Value ElemPtrV(Value* p, const Owned* arr) { return leafPtrV(K_PtrElem, p, arr); }

/* May the pointer be used (dereferenced)? Not outside its range: a pointer to a variable or a field only at offset 0, a pointer into an array or a buffer only
 * inside it - and not when the array or buffer is destroyed. Otherwise the exception is thrown (`err` has the value of the throw) and false is returned. With
 * `#performance` nothing is checked. */
FIRE_FN bool ptrValid(Value p, Value* err) {
#ifdef FIRE_UNCHECKED
    (void)p; (void)err;
    return true;
#else
    if (p.kind == K_PtrVar) {
        if (FIRE_UNLIKELY(FIRE_UNIT_OFFSET(p) != 0)) { *err = indexError("Pointer offset", FIRE_UNIT_OFFSET(p), 1); return false; }
        return true;
    }
    if (p.kind == K_PtrField) {
        if (FIRE_UNLIKELY((int16_t)p.reserved != 0)) { *err = indexError("Pointer offset", (int16_t)p.reserved, 1); return false; }
        return true;
    }
#ifdef FIRE_PTRBOUNDS
    {
        Owned* h;
        int64_t index, length;
        uint32_t slot = ptrSlot(p);
        intptr_t addr = (intptr_t)ptrAddr(p);
        if (FIRE_UNLIKELY(slotGen(slot) != FIRE_VGEN(p))) {
            Value leaf = Undef();
            leaf.kind = p.kind == K_PtrByte ? K_Buffer : K_Array;
            *err = destroyedError(leaf);
            return false;
        }
        h = slotHdr(slot);
        if (p.kind == K_PtrByte) { Buf* b = (Buf*)h; index = (int64_t)(addr - (intptr_t)b->data); length = b->length; }
        else { Arr* a = (Arr*)h; index = (int64_t)((addr - (intptr_t)arrItems(a)) / (intptr_t)sizeof(Value)); length = a->length; }
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
    if (FIRE_UNLIKELY(!isPtr(p))) fatal("Dereference of a value that is not a pointer.");
    if (FIRE_UNLIKELY(!ptrValid(p, &err))) return err;
    if (p.kind == K_PtrVar) return *(const Value*)p.p;
    if (p.kind == K_PtrField) { const FireFieldPtr* d = fieldPtrOf(p); return d->raw ? rawGetAt((const char*)p.p, d->raw, d->unit) : *(const Value*)p.p; }
    return p.kind == K_PtrByte ? Int(*(const uint8_t*)ptrAddr(p)) : *(const Value*)ptrAddr(p);
}

/* `p - q` for two pointers into the same array, buffer, variable or field: the number of elements between them. */
FIRE_FN Value ptrDiff(Value a, Value b) {
    if (a.kind != b.kind || (a.kind == K_PtrVar || a.kind == K_PtrField ? a.p != b.p : (ptrSlot(a) != ptrSlot(b) || FIRE_VGEN(a) != FIRE_VGEN(b))))
        fatal("Pointer difference ('ptr1 - ptr2') needs two pointers into the same array or the same variable.");
    if (a.kind == K_PtrVar) return Int((int64_t)FIRE_UNIT_OFFSET(a) - (int64_t)FIRE_UNIT_OFFSET(b));
    if (a.kind == K_PtrField) return Int((int64_t)(int16_t)a.reserved - (int64_t)(int16_t)b.reserved);
    return Int((int64_t)(((intptr_t)ptrAddr(a) - (intptr_t)ptrAddr(b)) / (a.kind == K_PtrByte ? (intptr_t)1 : (intptr_t)sizeof(Value))));
}

/* `*p = v`: the storage holds a count of what it holds now. */
FIRE_FN void ptrWrite(Value p, Value v) {
    Value err, old;
    Value* target;
    if (FIRE_UNLIKELY(!isPtr(p))) fatal("Assignment through a value that is not a pointer.");
    if (FIRE_UNLIKELY(!ptrValid(p, &err))) return;
    if (p.kind == K_PtrByte) { *(uint8_t*)ptrAddr(p) = (uint8_t)v.i; return; }
    if (p.kind == K_PtrField) {
        /* a field declared `defined`: checked like an assignment to it (undefined, another kind: the ArgumentException) */
        const FireFieldPtr* d = fieldPtrOf(p);
        char what[200];
        if (d->hasUnit) {
            v = adaptUnit(v, d->unit, false, d->declared == K_Float, d->text);
            if (FIRE_UNLIKELY(g_unwind.active)) return;
        }
        snprintf(what, sizeof what, "Field '%s'", d->name);
        v = guardKindNN(v, d->declared, d->unitless != 0, what, d->text);
        if (FIRE_UNLIKELY(v.kind != d->declared)) return;   /* (thrown) */
        if (d->declared == K_Int) v = intProgram(v);
        if (d->raw) { rawSetAt((char*)p.p, d->raw, v); return; }
        *(Value*)p.p = v;   /* (the field holds a number: nothing to count) */
        return;
    }
#ifdef FIRE_ELEMPTRS
    if (p.kind == K_PtrElem && (FIRE_UNCHECKED_OR(slotGen(ptrSlot(p)) == FIRE_VGEN(p)))) {
        /* an element of a typed array (unpacked when the pointer was taken): the value is converted like `a[i] = v`, or it is the exception */
        Arr* arr = (Arr*)slotHdr(ptrSlot(p));
        if (FIRE_UNLIKELY(arr->typed != NULL)) {
            bool ok;
            v = arrElemConvert(arr->typed, v, &ok);
            if (!ok) return;
        }
    }
#endif
    target = p.kind == K_PtrVar ? (Value*)p.p : (Value*)ptrAddr(p);
    old = *target;
    *target = v;
    retain(v);
    release(old);
}

/* A call site that passes an address to a method of which not every implementation takes a `ref`: the others get the value. */
FIRE_FN Value derefArg(Value v) { return isPtr(v) ? ptrRead(v) : v; }

FIRE_FN void requireRef(Value v, const char* name) {
    if (FIRE_UNLIKELY(!isPtr(v))) {
        fflush(stdout);
        fprintf(stderr, "fire runtime error: Parameter '%s' is declared 'ref': pass a variable, a field or an array element, not a value.\n", name);
        exitNow(1);
    }
}

/* `x[i]` as a `ref` argument. */
FIRE_FN Value addressOfIndex(Value a, Value i) {
    if (i.kind == K_Word && (a.kind == K_Array || a.kind == K_Buffer || a.kind == K_String)) i = Int((int64_t)(uint64_t)i.w);   /* (a word as an index: its bits as an int) */
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
        if (FIRE_UNLIKELY(b->elem != 1)) return fireError("ArgumentException", "A 'ref' argument 'x[i]' of a word buffer is not possible (only of an array or a byte buffer).");
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

/* ---- The bytes of an argument of a native (docs/packages/natives.md, "Bytes as arguments") ------------------------------------------------------
 * A native of a package never takes `bufOf(v)` of an argument unchecked: an array, a string or a number there would be read as a buffer (a crash). */
typedef struct FireBytes { const uint8_t* data; uint32_t length; } FireBytes;
/* (the copy of an array: a temporary of the pool, freed when the scope that called the native is left - like a string it made) */
typedef struct TempBytes { Ref h; uint32_t length; } TempBytes;

/* The bytes that a native READS from an argument (what it sends, writes, hashes, compresses, queues): everything that is bytes - a byte buffer (its own memory,
 * no copy), and an array of ints from 0 to 255 (a plain one or a typed one: a copy made for the call; it stays valid until the scope that called the native is
 * left - a native that keeps the bytes beyond the call copies them, as it does for a buffer). Anything else is the exception: another kind, an element that is
 * no int from 0 to 255 (ArgumentException), a destroyed buffer or array (DestroyedException) - then false, and the native returns at once:
 *     FireBytes b;
 *     if (!bytesArg(data, "Hash.Sha256.Of", &b)) return Undef();
 * `what` names the function in the message ("Hash.Sha256.Of expects bytes ..."). The VM passes the same values (fire_pkg_abi.h), so its texts are the same. */
FIRE_FN bool bytesArg(Value v, const char* what, FireBytes* out) {
    char text[240];
    out->data = NULL;
    out->length = 0;
    if (FIRE_LIKELY(v.kind == K_Buffer)) {
        Buf* b;
        if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return false; }
        b = bufOf(v);
        if (FIRE_UNLIKELY(b->elem != 1)) {
            /* a buffer of machine words is no bytes (its size depends on the target): the program says what it means (SPEC 8.10) */
            snprintf(text, sizeof text, "%s expects bytes (a byte buffer or an array of ints from 0 to 255), not a word buffer.", what);
            fireError("ArgumentException", text);
            return false;
        }
        out->data = b->data;
        out->length = b->length;
        return true;
    }
    if (v.kind == K_Array) {
        Arr* a;
        TempBytes* t;
        uint8_t* bytes;
        uint32_t i;
        if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return false; }
        a = arrOf(v);
        t = (TempBytes*)malloc(sizeof(TempBytes) + (size_t)a->length + 1);
        if (FIRE_UNLIKELY(!t)) allocFailed();
        bytes = (uint8_t*)(t + 1);
        for (i = 0; i < a->length; i++) {
            Value e = arrElemGet(a, i);
            if (FIRE_UNLIKELY(e.kind != K_Int || e.i < 0 || e.i > 255)) {
                free(t);
                if (e.kind == K_Int) snprintf(text, sizeof text, "%s: element %u of the array is %lld - a byte is an int from 0 to 255.", what, (unsigned)i, (long long)e.i);
                else if (e.kind == K_Undefined) snprintf(text, sizeof text, "%s: element %u of the array is undefined, not an int from 0 to 255.", what, (unsigned)i);
                else snprintf(text, sizeof text, "%s: element %u of the array is a %s, not an int from 0 to 255.", what, (unsigned)i, coerceKindName(e.kind));
                fireError("ArgumentException", text);
                return false;
            }
            bytes[i] = (uint8_t)e.i;
        }
        t->h.type = R_Bytes;
        t->length = a->length;
        registerTemp(&t->h, NULL);
        out->data = bytes;
        out->length = a->length;
        return true;
    }
    snprintf(text, sizeof text, "%s expects bytes (a byte buffer or an array of ints from 0 to 255), not %s.", what, v.kind == K_Undefined ? "undefined" : coerceKindName(v.kind));
    fireError("ArgumentException", text);
    return false;
}

/* The byte buffer that a native FILLS (what it reads, receives, decodes into - or changes in place): only a real buffer (`new byte[n]`); an array is an
 * ArgumentException (it would be filled in a copy that nobody sees), so is every other kind, a destroyed buffer a DestroyedException - then NULL, and the
 * native returns at once. */
FIRE_FN Buf* bufferArg(Value v, const char* what) {
    char text[240];
    if (FIRE_LIKELY(v.kind == K_Buffer)) {
        if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return NULL; }
        if (FIRE_UNLIKELY(bufOf(v)->elem != 1)) {
            snprintf(text, sizeof text, "%s expects a byte buffer (new byte[n]), not a word buffer.", what);
            fireError("ArgumentException", text);
            return NULL;
        }
        return bufOf(v);
    }
    if (v.kind == K_Array) snprintf(text, sizeof text, "%s expects a byte buffer (new byte[n]), not an array.", what);
    else snprintf(text, sizeof text, "%s expects a byte buffer (new byte[n]), not %s.", what, v.kind == K_Undefined ? "undefined" : coerceKindName(v.kind));
    fireError("ArgumentException", text);
    return NULL;
}

/* The machine words that a native READS from an argument (SPEC 8.10, docs/packages/natives.md): a buffer of words (`word a[]`, `new word[n]`: its own memory,
 * FIRE_WORD_BITS wide, no copy), or an array of ints and words (each cut to the width; a copy that stays valid until the scope that called the native is
 * left). Anything else - a byte buffer too - is the ArgumentException (an element that is no int or word as well), a destroyed one the DestroyedException:
 * then false, and the native returns at once:
 *     FireWords w;
 *     if (!wordsArg(data, "Pkg.Sum", &w)) return Undef();
 *     for (i = 0; i < w.length; i++) total += w.data[i];
 * The words of a buffer can also be written in place (`(FireWord*)w.data`) when the argument is a buffer - an array's copy goes nowhere. */
typedef struct FireWords { const FireWord* data; uint32_t length; } FireWords;
FIRE_FN bool wordsArg(Value v, const char* what, FireWords* out) {
    char text[240];
    out->data = NULL;
    out->length = 0;
    if (FIRE_LIKELY(v.kind == K_Buffer)) {
        Buf* b;
        if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return false; }
        b = bufOf(v);
        if (FIRE_UNLIKELY(b->elem != sizeof(FireWord))) {
            snprintf(text, sizeof text, "%s expects words (a word buffer or an array of ints), not a byte buffer.", what);
            fireError("ArgumentException", text);
            return false;
        }
        out->data = bufWords(b);
        out->length = b->length;
        return true;
    }
    if (v.kind == K_Array) {
        Arr* a;
        TempBytes* t;
        FireWord* words;
        size_t head = (sizeof(TempBytes) + 7u) & ~(size_t)7u;
        uint32_t i;
        if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return false; }
        a = arrOf(v);
        t = (TempBytes*)malloc(blockSize(head + 1, a->length, sizeof(FireWord)));
        if (FIRE_UNLIKELY(!t)) allocFailed();
        words = (FireWord*)(void*)((uint8_t*)t + head);
        for (i = 0; i < a->length; i++) {
            Value e = arrElemGet(a, i);
            if (FIRE_UNLIKELY(e.kind != K_Int && e.kind != K_Word)) {
                free(t);
                if (e.kind == K_Undefined) snprintf(text, sizeof text, "%s: element %u of the array is undefined, not an int or a word.", what, (unsigned)i);
                else snprintf(text, sizeof text, "%s: element %u of the array is a %s, not an int or a word.", what, (unsigned)i, coerceKindName(e.kind));
                fireError("ArgumentException", text);
                return false;
            }
            words[i] = e.kind == K_Word ? (FireWord)e.w : (FireWord)(uint64_t)(int64_t)e.i;
        }
        t->h.type = R_Bytes;
        t->length = a->length;
        registerTemp(&t->h, NULL);
        out->data = words;
        out->length = a->length;
        return true;
    }
    snprintf(text, sizeof text, "%s expects words (a word buffer or an array of ints), not %s.", what, v.kind == K_Undefined ? "undefined" : coerceKindName(v.kind));
    fireError("ArgumentException", text);
    return false;
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
    r = allocStr((uint32_t)bufSize(b), list);   /* (a word buffer: all its bytes) */
    d = strChars(r);
    for (i = 0; i < (uint32_t)bufSize(b); i++) d[i] = b->data[i];
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
    if (FIRE_UNLIKELY((uint32_t)bufSize(b) % (uint32_t)width.i != 0)) fatal("The buffer size is not a multiple of the character width.");
    count = (uint32_t)bufSize(b) / (uint32_t)width.i;
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
    if (FIRE_UNLIKELY((uint32_t)bufSize(b) < (uint32_t)width.i)) fatal("The buffer is too small for the character width.");
    return Char(readCodeUnit(b->data, (int)width.i, b->little));
}
/* `buffer.ToLittleEndian()` / `ToBigEndian()`: a copy, mirrored as one block when the order differs. A buffer of words: a byte buffer with each word in that
 * order (the words stay where they are: their serialized form). */
FIRE_FN Value bufToEndian(Value v, bool little, OwnList* list) {
    Buf* b;
    Buf* c;
    uint32_t i, n, e;
    if (!ownedOf(v)) return destroyedError(v);
    b = bufOf(v);
    n = (uint32_t)bufSize(b);
    e = b->elem;
    c = allocBuf(n, list);
    if ((b->little != 0) == little) dataCopy(c->data, b->data, n);
    else if (e > 1) { for (i = 0; i < n; i++) c->data[i] = b->data[(i / e) * e + (e - 1 - i % e)]; }
    else for (i = 0; i < n; i++) c->data[i] = b->data[n - 1 - i];
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
    ownLink(objOwned(target), o);
}

/* `object is from owner` / `object is under owner` (SPEC 6): the owner of the object is that object - directly, or anywhere up the chain. */
FIRE_FN bool isFrom(Value operand, Value owner, bool transitive) {
    Owned* target;
    OwnList* l;
    if (FIRE_UNLIKELY(operand.kind != K_Class || owner.kind != K_Class)) fatal("'is from'/'is under' expects objects.");
    if (FIRE_UNLIKELY(!leafAlive(operand) || !leafAlive(owner))) return false;   /* (a destroyed object owns nothing and belongs to nothing, like in the VM) */
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
}

/* `f(g())`: the result of a call passed straight on belongs to the callee, not to the caller (SPEC 2.1). The generated code lets the value travel in the
 * argument list `al` of the call and destroys what is left in it afterwards (the callee has moved, stored or returned what it wants to keep). */
FIRE_FN OwnList* scopeListOf(const Owned* o);
FIRE_FN void reownArg(Value v, OwnList* from, OwnList* al) {
    Owned* o = ownedOf(v);
    if (o && scopeListOf(o) == from) { ownUnlink(o); ownLink(al, o); }   /* (also what hangs on a temporary object) */
    al->parent = from;
    al->mark = AL_MARK;
}

/* ---- Ownership in property accessors (SPEC 2.2): what belongs to the running accessor call - in one of its scopes `lists`, or handed to a setter by the
 * property assignment of another accessor (`mine`, the argument list it found in g_acc.handOff on entry) - goes to `this` when it is assigned to a field of
 * `this`, and to the setter like `take x` when it is assigned to a property. */
FIRE_FN Owned* accMine(Value v, OwnList* const* lists, int n, OwnList* mine) {
    Owned* o = ownedOf(v);
    int i;
    if (!o || !o->owner) return NULL;
    if (mine && o->owner == mine) return o;
    for (i = 0; i < n; i++) if (o->owner == lists[i]) return o;
    return NULL;
}
/* `this.field = v` (after the store): `this` takes v over; a getter (`track`) remembers it for its `return` (accReturn). */
FIRE_FN void accKeep(Value self, Value obj, Value v, OwnList* const* lists, int n, OwnList* mine, bool track) {
    Owned* o;
    if (obj.kind != K_Class || self.kind != K_Class || !sameLeaf(obj, self) || !leafAlive(self)) return;
    o = accMine(v, lists, n, mine);
    if (!o || o == &asObj(self)->h) return;
    takeToObject(o, asObj(self));
    if (!track || !objOwns(asObj(self), o)) return;
    if (g_acc.top == g_acc.cap) {
        g_acc.cap = g_acc.cap ? g_acc.cap * 2 : 16;
        g_acc.items = (AccEntry*)realloc(g_acc.items, g_acc.cap * sizeof(AccEntry));
        if (!g_acc.items) allocFailed();
    }
    g_acc.items[g_acc.top].o = o;
    g_acc.items[g_acc.top].slot = o->slot;
    g_acc.items[g_acc.top].gen = o->gen;
    g_acc.top++;
}
/* `return r` of a getter (`base`: g_acc.top when it started): a value its `this` took over in this call belongs to the call again (`to`: its function scope),
 * so that it goes to the caller like a returned local. `done`: the function ends now (not a `finally` first) - it forgets what this call remembered. */
FIRE_FN void accReturn(Value r, Value self, uint32_t base, OwnList* to, bool done) {
    Owned* o = ownedOf(r);
    uint32_t i;
    if (o && self.kind == K_Class && leafAlive(self) && objOwns(asObj(self), o))
        for (i = base; i < g_acc.top; i++)
            if (g_acc.items[i].o == o && g_acc.items[i].slot == o->slot && g_acc.items[i].gen == o->gen) { ownUnlink(o); ownLink(to, o); break; }
    if (done && g_acc.top > base) g_acc.top = base;
}
/* `obj.Prop = v`: v travels in the argument list `al` of the setter call (like `take x`); the setter finds `al` in g_acc.handOff. Returns where v was (NULL:
 * not the accessor's, nothing handed over). */
FIRE_FN OwnList* accHandOver(Value v, OwnList* const* lists, int n, OwnList* mine, OwnList* from, OwnList* al) {
    Owned* o = accMine(v, lists, n, mine);
    OwnList* was;
    if (!o) return NULL;
    was = o->owner;
    takeArg(v, from, al);
    g_acc.handOff = al;
    return was;
}
/* ... after the call: when no setter took the hand-over (the name was a field of that object), v goes back to where it was (`giveBack`). The caller then
 * finishes `al` (finishArgs): what the setter did not keep dies. */
FIRE_FN void accHandEnd(Value v, OwnList* was, OwnList* al, bool giveBack) {
    if (giveBack && was && g_acc.handOff == al) {
        Owned* o = ownedOf(v);
        if (o && o->owner == al) { ownUnlink(o); ownLink(was, o); }
    }
    g_acc.handOff = NULL;
}
/* The start of a setter: the hand-over is its own (NULL: none). */
FIRE_FN OwnList* accTakeHandOff(void) { OwnList* l = g_acc.handOff; g_acc.handOff = NULL; return l; }

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
/* (An object: its list as it is - NULL when it owns nothing yet; moveReachable makes it only when something really moves, see fire_carrierList.) */
FIRE_FN OwnList* carrierOf(Owned* node) {
    if (node->okind == O_Object) return ((Obj*)node)->ext ? &((Obj*)node)->ext->owned : NULL;
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
extern FIRE_RT_DATA EnumerateItemsFn g_enumerateItems;

/* Where what moves goes: a list, or an object whose list is made when the first value moves into it (most objects own nothing). */
typedef struct fire_Carrier { OwnList* list; Obj* obj; } fire_Carrier;
FIRE_FN OwnList* fire_carrierList(fire_Carrier* c) { if (!c->list && c->obj) c->list = objOwned(c->obj); return c->list; }
FIRE_FN bool fire_carrierHas(const fire_Carrier* c) { return c->list != NULL || c->obj != NULL; }
FIRE_FN Owned* fire_carrierHolder(const fire_Carrier* c) { return c->obj ? &c->obj->h : c->list ? c->list->holder : NULL; }
/* Moves `child` there (unless it would end up below itself). */
FIRE_FN void fire_carrierTake(fire_Carrier* c, Owned* child) {
    Owned* holder;
    bool cycle;
    if (!fire_carrierHas(c) || (c->list && child->owner == c->list)) return;
    holder = fire_carrierHolder(c);
    cycle = child->okind == O_Object && holder && ownsTransitively(child, holder);
    if (!cycle) { OwnList* l = fire_carrierList(c); if (child->owner) ownUnlink(child); ownLink(l, child); }
}
typedef struct fire_MoveSink { fire_Carrier* carrier; OwnedStack* seen; } fire_MoveSink;
FIRE_FN void fire_moveSinkItem(Value v, void* ctx) {
    fire_MoveSink* k = (fire_MoveSink*)ctx;
    Owned* child = ownedOf(v);
    if (!child || (child->flags & (1 | F_VISIT))) return;
    child->flags |= F_VISIT;
    ownedStackPush(k->seen, child);
    fire_carrierTake(k->carrier, child);
}

/* Takes what hangs on `root` along (the root has its new owner already), by `mode`: TK_CHILDREN what it points to directly, TK_LOCALS everything reachable that
 * belongs to a scope of the call (recursively), TK_ALL everything reachable. A thing that moves belongs to the object that points to it; what would end up
 * below itself stays where it is. Every node is visited once. TK_CHILDREN of an IEnumerable takes its items (through the enumerator, which lives in `here`,
 * null: none) instead of its fields. */
/* One value that hangs on a node of moveReachable: it moves to `carrier` (by the mode) and is walked further. A lambda owns nothing: what it captured hangs
 * on the holder of the lambda. */
FIRE_FN void fire_moveChild(Value v, fire_Carrier* carrier, int mode, const LocalScopes* locals, OwnedStack* work, OwnedStack* seen) {
    Owned* child = ownedOf(v);
    bool move, descend;
    if (!child) {
        if (v.kind == K_Lambda) {
            Lam* l = (Lam*)v.p;
            uint32_t i;
            for (i = 0; i < l->ncaps; i++) fire_moveChild(lamCaps(l)[i], carrier, mode, locals, work, seen);
        }
        return;
    }
    if (child->flags & (1 | F_VISIT)) return;
    child->flags |= F_VISIT;
    ownedStackPush(seen, child);
    move = mode != TK_LOCALS || isLocalNode(child, locals);
    descend = mode == TK_ALL || (mode == TK_LOCALS && (move || (child->owner && child->owner->holder && (child->owner->holder->flags & F_VISIT))));
    if (move) fire_carrierTake(carrier, child);
    if (descend) ownedStackPush(work, child);
}

FIRE_FN void moveReachable(Owned* root, int mode, const LocalScopes* locals, OwnList* here) {
    OwnedStack work = {NULL, 0, 0}, seen = {NULL, 0, 0};
    size_t k;
    if (mode == TK_THIS) return;
    root->flags |= F_VISIT;
    ownedStackPush(&seen, root);
    ownedStackPush(&work, root);
    while (work.count) {
        Owned* node = work.items[--work.count];
        fire_Carrier carrier;
        Value* items;
        uint32_t n, i;
        carrier.list = carrierOf(node);
        carrier.obj = node->okind == O_Object ? (Obj*)node : NULL;
        if (mode == TK_CHILDREN && node->okind == O_Object && g_enumerateItems && here) {
            fire_MoveSink sink;
            sink.carrier = &carrier;
            sink.seen = &seen;
            if (g_enumerateItems(ObjV((Obj*)node), here, fire_moveSinkItem, &sink)) continue;
        }
        if (node->okind == O_Object) { Obj* ob = (Obj*)node; items = objFields(ob); n = ob->nfields; }
        else if (node->okind == O_Array) { Arr* a = (Arr*)node; items = arrValues(a); n = items ? a->length : 0; }   /* (a packed array holds numbers only) */
        else continue;
        for (i = 0; i < n; i++) fire_moveChild(items[i], &carrier, mode, locals, &work, &seen);
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
    if (!o) {
        if (v.kind == K_Lambda) {   /* (a lambda owns nothing, but what it captured from the call goes along with it) */
            Lam* l = (Lam*)v.p;
            uint32_t i;
            for (i = 0; i < l->ncaps; i++) transferTree(lamCaps(l)[i], lists, n);
        }
        return;
    }
    scopes.lists = lists;
    scopes.n = n;
    if (!isLocalNode(o, &scopes)) return;
    ownUnlink(o);   /* (the caller takes it over: adopt) */
    moveReachable(o, TK_LOCALS, &scopes, NULL);
}

/* After a call with arguments that travelled in `al` (SPEC 2.1: they belong to the call): what the call returned freshly takes along what still hangs on it
 * in `al` (`return new W(arg)`, a lambda that captured an argument - like the scopes of the call at its `return`), the rest dies. */
FIRE_FN void fire_carryFromArgs(Value v, OwnList* al) {
    Owned* o = ownedOf(v);
    LocalScopes scopes;
    if (o) {
        if (o->owner) return;   /* (only a value the call handed back: on its way to the caller's `adopt`) */
        scopes.lists = &al;
        scopes.n = 1;
        moveReachable(o, TK_LOCALS, &scopes, NULL);
    } else if (v.kind == K_Lambda) {
        Lam* l = (Lam*)v.p;
        uint32_t i;
        for (i = 0; i < l->ncaps; i++) fire_carryFromArgs(lamCaps(l)[i], al);
    }
}
FIRE_FN void finishArgs(Value result, OwnList* al) {
    g_argIn = NULL;   /* (`try x.Take...`: a callee that was no fire function did not take it) */
    transferOut(result, al);
    if (al->head) fire_carryFromArgs(result, al);
    if (al->head) destroyList(al);
}

/* ---- The first slot owns an unclaimed value (SPEC 2.1) ----
 * `new`, literals, copies and what a call hands back belong to the statement temporaries of the current scope (a list `from` with `home` set) until a slot
 * claims them; the end of the statement leaves the temporaries (`leave`: their mark keeps the pool, which belongs to the scope). */

/* A slot claims `o` if it is still in the temporaries `from`: it goes to `to`, and what hangs on it and is still in `from` goes along (to the object that
 * points to it - the inner copies of a `copy`, the parts of a literal). */
FIRE_FN void claimTree(Owned* o, OwnList* from, OwnList* to) {
    LocalScopes scopes;
    if (!o || scopeListOf(o) != from || !to) return;   /* (also what hangs on a temporary object: `Type.Of(c).Find("x")`) */
    ownUnlink(o);
    ownLink(to, o);
    if (from->head) { scopes.lists = &from; scopes.n = 1; moveReachable(o, TK_LOCALS, &scopes, NULL); }
}
/* `var v = E`, `v = E`, a global: the scope's list claims an unclaimed value. */
FIRE_FN void claimToList(Value v, OwnList* from, OwnList* to) {
    if (v.kind == K_Lambda) {   /* a lambda owns nothing: the slot claims what it captured and is still in the temporaries */
        Lam* l = (Lam*)v.p;
        uint32_t i;
        for (i = 0; i < l->ncaps; i++) claimToList(lamCaps(l)[i], from, to);
        return;
    }
    claimTree(ownedOf(v), from, to);
}
/* `obj.f = E`, `arr[i] = E`, a field initializer: the object (the array) claims an unclaimed value. */
FIRE_FN void claimToHolder(Value holder, Value v, OwnList* from) {
    Owned* o = ownedOf(v);
    Owned* h = ownedOf(holder);
    LocalScopes scopes;
    Arr* a;
    if (!o && v.kind == K_Lambda && h && h->okind != O_Buffer) {   /* (what a stored lambda captured belongs to the holder) */
        Lam* l = (Lam*)v.p;
        uint32_t i;
        for (i = 0; i < l->ncaps; i++) claimToHolder(holder, lamCaps(l)[i], from);
        return;
    }
    if (!o || scopeListOf(o) != from || !h || h == o || h->okind == O_Buffer) return;
    if (h->okind == O_Object) {
        takeToObject(o, (Obj*)h);
        if (from->head && scopeListOf(o) != from) { scopes.lists = &from; scopes.n = 1; moveReachable(o, TK_LOCALS, &scopes, NULL); }
        return;
    }
    a = (Arr*)h;
    if (!a->parts) a->parts = newPartsList(&a->h);
    claimTree(o, from, a->parts);
}
/* An array literal: its elements are slots of the array - unclaimed ones belong to it. */
FIRE_FN void claimElements(Value arr, OwnList* from) {
    Owned* h = ownedOf(arr);
    Value* items;
    uint32_t i;
    if (!h || h->okind != O_Array || !from->head) return;
    items = arrValues((Arr*)h);
    if (!items) return;
    for (i = 0; i < ((Arr*)h)->length; i++) claimToHolder(arr, items[i], from);
}
/* A claim site of a setter (`obj.f = value`, after the store): the value goes to `obj` if the setter call still owns it (it is in the setter's argument list). */
FIRE_FN void claimKeep(Value obj, Value v, OwnList* mine) {
    Owned* o = ownedOf(v);
    if (!mine || !o || o->owner != mine || obj.kind != K_Class || !leafAlive(obj) || o == &asObj(obj)->h) return;
    takeToObject(o, asObj(obj));
}
/* A claim site of a setter (`Store(value)`): the value goes to the call (its argument list `al`) if the setter call still owns it. */
FIRE_FN void claimArg(Value v, OwnList* mine, OwnList* here, OwnList* al) {
    Owned* o = ownedOf(v);
    if (mine && o && o->owner == mine) takeArg(v, here, al);
}

enum { OM_TakeLocal, OM_TakeUpwards, OM_TakeGlobal, OM_TakeTo };

/* `x.TakeLocal()`, `x.TakeUpwards()`, `x.TakeGlobal()`, `x.TakeTo(obj)` on an object, an array or a buffer (SPEC 2.2). */
FIRE_FN void ownMethod(int method, Value self, Value arg, OwnList* here) {
    Owned* o = ownedOf(self);
    if (!o) fatal("Take... is only possible for an object, an array or a buffer that is not destroyed.");
    if (o->flags & 1) fatal("A destroyed value cannot change its owner.");
    switch ((int)method) {
        case OM_TakeLocal:
            if (here->home) here = here->home;   /* (the scope itself, not its statement temporaries) */
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
/* Does the running call own `o` (`try x.Take...`, `try take x`)? It belongs to a scope of the call (`lists`: its scopes), to the argument list the call was
 * given (`myArgs`) or to the current object (`thisValue`). */
FIRE_FN bool ownedByCall(Owned* o, OwnList* const* lists, int n, Value thisValue, OwnList* myArgs) {
    int i;
    if (!o || (o->flags & 1)) return false;
    for (i = 0; i < n; i++) if (o->owner == lists[i] || (lists[i] && o->owner && o->owner->home == lists[i])) return true;
    /* the result of a call that was passed on (`f(g())`, `f(take x)`) belongs to the called function: natively it travels in the argument list of the call -
     * the one the running function was given (`myArgs`: the caller set g_argIn right before the call, the function took it on entry) */
    if (myArgs && o->owner == myArgs) return true;
    return thisValue.kind == K_Class && leafAlive(thisValue) && objOwns(objAt(thisValue), o);
}

/* `try take x` (SPEC 2.2): true when the running call owns x (then the take that follows moves it; otherwise nothing changes). */
FIRE_FN bool tryTakeOwned(Value v, OwnList* const* lists, int n, Value thisValue, OwnList* myArgs) { return ownedByCall(ownedOf(v), lists, n, thisValue, myArgs); }

FIRE_FN bool ownMethodTry(int method, Value self, Value arg, Value mode, OwnList* here, OwnList* const* lists, int n, Value thisValue, OwnList* myArgs) {
    Owned* o = ownedOf(self);
    if (!o) fatal("Take... is only possible for an object, an array or a buffer that is not destroyed.");
    if (!ownedByCall(o, lists, n, thisValue, myArgs)) return false;
    ownMethodT(method, self, arg, mode, here, lists, n);
    return true;
}

/* ---------------------------------------------------------------------------------------------------------------------
 * `flat x` and `copy x` (SPEC 2.4): no constructor runs, the copy belongs to `owner` like any new value.
 * --------------------------------------------------------------------------------------------------------------------- */
/* `[NoCopy]` on the class or a base class (FireClassDesc.flags 1): flat/copy never duplicate an instance of it. */
FIRE_FN bool classNoCopy(uint32_t cls) { return cls < g_classDescCount && (g_classDescs[cls].flags & 1u) != 0; }
/* ... an ArgumentException instead (the text of RuntimeClass.NoCopyMessage in the VM). `op`: NULL for flat/copy, else the copy of a thread
 * ("taking", "fire global", "sync" - sync always reaches it through a field or an element). */
FIRE_COLD FIRE_FN Value noCopyErrorOp(uint32_t cls, bool nested, const char* op) {
    char text[400];
    const char* name = cls < g_classDescCount && g_classDescs[cls].name ? g_classDescs[cls].name : "?";
    if (!op) {
        if (nested) snprintf(text, sizeof text, "'%s' cannot be copied ([NoCopy]) - 'copy' reaches an instance of it (a field or an element of the copied value); it exists only once, share the reference instead.", name);
        else snprintf(text, sizeof text, "'%s' cannot be copied ([NoCopy]) - an instance of it exists only once; share the reference or pass it on with 'take'.", name);
    } else if (nested || strcmp(op, "sync") == 0)
        snprintf(text, sizeof text, "'%s' cannot be copied ([NoCopy]) - '%s' reaches an instance of it (a field or an element of the copied value), which exists only once.", name, op);
    else snprintf(text, sizeof text, "'%s' cannot be copied ([NoCopy]) - '%s' would copy an instance of it, which exists only once.", name, op);
    return fireError("ArgumentException", text);
}
FIRE_COLD FIRE_FN Value noCopyError(uint32_t cls, bool nested) { return noCopyErrorOp(cls, nested, NULL); }
/* The operand of flat/copy is a destroyed object (its memory is freed: nothing of it can be read). */
FIRE_COLD FIRE_FN Value copyDestroyedError(void) { return fireError("ArgumentException", "An already destroyed object cannot be copied."); }

FIRE_FN Value flatCopy(Value v, OwnList* owner) {
    uint32_t i;
    switch (v.kind) {
        case K_Class: {
            Obj* src;
            Value c;
            Obj* dst;
            if (FIRE_UNLIKELY(!leafAlive(v))) return copyDestroyedError();
            src = asObj(v);
            if (FIRE_UNLIKELY(classNoCopy(src->cls))) return noCopyError(src->cls, false);
            c = newObjectR(src->cls, src->nfields, src->nraw, owner);
            dst = asObj(c);
            objSetTArgs(dst, objTArgs(src));
            for (i = 0; i < src->nfields; i++) { objFields(dst)[i] = objFields(src)[i]; retain(objFields(dst)[i]); }
            if (src->nraw) memcpy(objRaw(dst), objRaw(src), src->nraw);   /* (the packed fields: numbers) */
            return c;
        }
        case K_Array: {
            Arr* src;
            Arr* dst;
            if (!ownedOf(v)) return destroyedError(v);
            src = arrOf(v);
            if ((dst = packedCopy(src, owner)) != NULL) return ArrV(dst);
            dst = allocArr(src->length, owner);
            if (!arrCopyTyped(dst, src))   /* (a copy of a typed array is typed; a packed one copies its numbers) */
                for (i = 0; i < src->length; i++) { arrItems(dst)[i] = arrItems(src)[i]; retain(arrItems(dst)[i]); }
            return ArrV(dst);
        }
        case K_Buffer: {
            Buf* src;
            Buf* dst;
            if (!ownedOf(v)) return destroyedError(v);
            src = bufOf(v);
            dst = bufCopy(src, owner);
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

/* ... the same reason found before anything is copied (the check of `fire ... taking x`): the ArgumentException, the thread does not start. */
FIRE_COLD FIRE_FN Value takingRefused(const char* what) {
    char text[200];
    snprintf(text, sizeof text, "'taking' rejected: %s", what);
    return fireError("ArgumentException", text);
}

/* A growing array of anything (`elem` bytes per item). */
FIRE_FN void* fire_growPush(void* items, uint32_t* count, uint32_t* cap, size_t elem) {
    if (*count == *cap) {
        uint32_t next = *cap ? *cap * 2 : 16;
        if (FIRE_UNLIKELY((uint64_t)next * elem > (uint64_t)SIZE_MAX)) allocFailed();   /* (a 16-bit size_t: a list larger than 64 KB) */
        *cap = next;
        items = realloc(items, (size_t)next * elem);
        if (!items) allocFailed();
    }
    return items;
}

typedef struct DeepCopier {
    OwnList* owner;
    bool taking;          /* a copy for a fire thread (`taking`): everything reachable is copied (whoever owns it), copies are marked; no lambdas/pointers */
    bool noCopy;          /* `copy x` (and the arguments of a job): an instance of a `[NoCopy]` class is an ArgumentException */
    const char* noCopyOp; /* ... its text: NULL for copy, else "taking"/"fire global"/"sync" (noCopyErrorOp) */
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
    if (v.kind != K_Class && v.kind != K_Array && !(c->taking && (v.kind == K_Lambda || isPtr(v)))) return;   /* nothing to discover */
    c->pending = (Value*)fire_growPush(c->pending, &c->pendingCount, &c->pendingCap, sizeof(Value));
    c->pending[c->pendingCount++] = v;
}
FIRE_FN bool copierSeenArray(DeepCopier* c, const void* a) {
    uint32_t i;
    for (i = 0; i < c->seenCount; i++) if (c->seen[i] == a) return true;
    return false;
}
/* Phase 1: everything reachable through fields and array elements (objects in discovery order); false: an instance of a `[NoCopy]` class is among them
 * (the ArgumentException is thrown, nothing is copied yet). A destroyed object stays a shared reference (its memory is freed). */
FIRE_FN bool copierDiscover(DeepCopier* c, Value start) {
    uint32_t i;
    copierPushPending(c, start);
    while (c->pendingCount) {
        Value v = c->pending[--c->pendingCount];
        if (c->taking && (v.kind == K_Lambda || isPtr(v))) {
            const char* what = v.kind == K_Lambda ? "lambda values cannot be copied for a thread." : "raw pointers cannot be copied for a thread.";
            if (c->noCopy) { takingRefused(what); return false; }   /* the check before `taking` (noCopyFree): the ArgumentException */
            takingViolation(what);
        }
        if (v.kind == K_Class) {
            Obj* o;
            if (!leafAlive(v)) continue;
            o = asObj(v);
            if (copyMapFind(&c->objects, o)) continue;
            if (FIRE_UNLIKELY(c->noCopy && classNoCopy(o->cls))) { noCopyErrorOp(o->cls, start.kind != K_Class || o != asObj(start), c->noCopyOp); return false; }
            copyMapPut(&c->objects, o, Undef());   /* marks it as found; the copy is made in phase 2 */
            c->order = (Obj**)fire_growPush(c->order, &c->orderCount, &c->orderCap, sizeof(Obj*));
            c->order[c->orderCount++] = o;
            for (i = 0; i < o->nfields; i++) copierPushPending(c, objFields(o)[i]);
        } else if (v.kind == K_Array && ownedOf(v)) {
            Arr* a = arrOf(v);
            if (copierSeenArray(c, a)) continue;
            c->seen = (const void**)fire_growPush((void*)c->seen, &c->seenCount, &c->seenCap, sizeof(const void*));
            c->seen[c->seenCount++] = a;
            if (arrValues(a)) for (i = 0; i < a->length; i++) copierPushPending(c, arrValues(a)[i]);   /* (a packed array holds numbers only) */
        }
    }
    return true;
}
/* Phase 2: the copy of an object belongs to the copy of its owner when that is copied too, else to `owner`. */
FIRE_FN Value copierMaterialize(DeepCopier* c, Obj* o) {
    Value* done = copyMapFind(&c->objects, o);
    OwnList* target = c->owner;
    Value copy;
    if (done && done->kind == K_Class) return *done;
    if (o->h.owner && o->h.owner->holder && copyMapFind(&c->objects, o->h.owner->holder)) {
        Value holderCopy = copierMaterialize(c, (Obj*)o->h.owner->holder);
        target = objOwned(asObj(holderCopy));
    }
    copy = newObjectR(o->cls, o->nfields, o->nraw, target);
    objSetTArgs(asObj(copy), objTArgs(o));
    if (c->taking) asObj(copy)->h.flags |= 16;   /* a copy for a thread: its destructor does not run when the thread ends */
    *copyMapFind(&c->objects, o) = copy;   /* replaces the "found" marker */
    return copy;
}
/* What a value becomes in the copy. */
FIRE_FN Value copierMap(DeepCopier* c, Value v) {
    uint32_t i;
    switch (v.kind) {
        case K_Class: { Value* m = leafAlive(v) ? copyMapFind(&c->objects, asObj(v)) : NULL; return m && m->kind == K_Class ? *m : v; }
        case K_Array: {
            Arr* src;
            Arr* dst;
            Value* m;
            Value r;
            if (!ownedOf(v)) return v;
            src = arrOf(v);
            m = copyMapFind(&c->containers, src);
            if (m) return *m;
            if ((dst = packedCopy(src, c->owner)) != NULL) { r = ArrV(dst); copyMapPut(&c->containers, src, r); return r; }   /* (numbers only: nothing inside to copy) */
            dst = allocArr(src->length, c->owner);
            r = ArrV(dst);
            copyMapPut(&c->containers, src, r);   /* before filling: an array may contain itself */
            if (!arrCopyTyped(dst, src))   /* (a copy of a typed array is typed; a packed one copies its numbers) */
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
            dst = bufCopy(src, c->owner);
            r = BufV(dst);
            copyMapPut(&c->containers, src, r);
            return r;
        }
        default: return v;
    }
}
FIRE_FN Value copierRun(DeepCopier* c, Value root) {
    uint32_t i, f;
    if (!copierDiscover(c, root)) return Undef();
    for (i = 0; i < c->orderCount; i++) copierMaterialize(c, c->order[i]);
    for (i = 0; i < c->orderCount; i++) {
        Obj* src = c->order[i];
        Obj* dst = asObj(*copyMapFind(&c->objects, src));
        for (f = 0; f < src->nfields; f++) { objFields(dst)[f] = copierMap(c, objFields(src)[f]); retain(objFields(dst)[f]); }
        if (src->nraw) memcpy(objRaw(dst), objRaw(src), src->nraw);   /* (the packed fields: numbers) */
    }
    return copierMap(c, root);
}

/* `copy x`: the copy of everything reachable from x, once each (cycles included); what belonged to a copied object belongs to its copy. */
FIRE_FN Value deepCopy(Value v, OwnList* owner) {
    if (v.kind == K_Class && FIRE_UNLIKELY(!leafAlive(v))) return copyDestroyedError();
    if (v.kind == K_Class || ((v.kind == K_Array || v.kind == K_Buffer) && ownedOf(v))) {
        DeepCopier c;
        Value r;
        copierInit(&c, owner);
        c.noCopy = true;
        r = copierRun(&c, v);
        copierFree(&c);
        return r;
    }
    if (v.kind == K_Array || v.kind == K_Buffer) return destroyedError(v);
    return v;
}

FIRE_FN Value copyValue(Value v, bool deep, OwnList* owner) { return deep ? deepCopy(v, owner) : flatCopy(v, owner); }

/* `[NoCopy]` before a copy for another thread (`taking`, the arguments of `fire global`, case A of `sync`): false - the ArgumentException is thrown - when
 * an instance of a `[NoCopy]` class is reachable from v through fields and array elements. Only looks (the discovery of a deep copy), so it runs before
 * anything is copied, queued or started; the VM walks the same way (ObjectCopier.RequireNoCopyFree). */
FIRE_FN bool noCopyFree(Value v, const char* op) {
    DeepCopier c;
    bool ok;
    if (v.kind != K_Class && !(v.kind == K_Array && ownedOf(v))) return true;
    copierInit(&c, NULL);
    c.noCopy = true;
    c.noCopyOp = op;
    c.taking = strcmp(op, "taking") == 0;   /* (a lambda or a pointer in it cannot be copied for a thread either) */
    ok = copierDiscover(&c, v);
    copierFree(&c);
    return ok;
}

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
    return copyValue(v, deep, objOwned(t));
}

/* A copy-prefixed argument (`f(copy x)`): the copy travels in the argument list of the call and dies with it unless the callee keeps it. */
FIRE_FN Value copyArg(Value v, bool deep, OwnList* from, OwnList* al) {
    al->parent = from;
    al->mark = AL_MARK;
    return copyValue(v, deep, al);
}

/* `delete x`: destroys the object (destructor, everything it owns), array or buffer at once. */
FIRE_FN void deleteValue(Value v) {
    if (v.kind == K_Class) {
        Obj* o;
        if (!leafAlive(v)) return;   /* already dead: nothing to do (like in the VM) */
        o = objAt(v);
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
        case K_Word: return (int64_t)v.w;   /* (a machine word: its bits) */
        case K_Float: return (int64_t)v.f;
        default: fatal("An extern function was called with an argument that is not a number.");
    }
    FIRE_NOT_REACHED(0)
}
FIRE_FN double externFloat(Value v) {
    if (v.kind == K_Float) return (double)v.f;
    if (v.kind == K_Int) return (double)v.i;
    if (v.kind == K_Word) return (double)v.w;
    fatal("An extern function was called with an argument that is not a number.");
    FIRE_NOT_REACHED(0)
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
    p = (char*)malloc(blockSize(1, s->length, 4));
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
/* `byte name[]` of an `inline c` function: the bytes of the buffer and their number (null and 0 for `undefined`). A destroyed buffer is the exception (and
 * null/0 for the call the generated code still makes before it looks at the exception). */
FIRE_FN uint8_t* externBytes(Value v, uint32_t* length) {
    *length = 0;
    if (v.kind == K_Undefined) return NULL;
    if (FIRE_UNLIKELY(v.kind != K_Buffer)) fatal("An inline C function expects a byte buffer (byte[]).");
    if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return NULL; }
    *length = bufOf(v)->length;
    return bufOf(v)->data;
}

/* A pointer argument: a pointer to a variable gets 8 bytes of native memory with its value (and the new value is written back after the call). */
typedef struct ExternPtr {
    union { unsigned char bytes[8]; int64_t align; } buf;
    Value* target;
    uint8_t kind;
    void* raw;
    Value field;   /* a K_PtrField (a field declared `defined`): the new value goes back through it (checked like an assignment) */
} ExternPtr;
FIRE_FN void externPtrInit(ExternPtr* e, Value v) {
    Value cur;
    memset(e, 0, sizeof *e);
    if (v.kind == K_Undefined) return;
    if (v.kind == K_Int) { e->raw = (void*)(intptr_t)v.i; return; }   /* an address that was handed over as a number */
    if (!isPtr(v)) fatal("A pointer argument of an extern function expects a pointer.");
    if (v.kind == K_PtrByte) { e->raw = ptrAddr(v); return; }   /* a pointer into a byte buffer: its bytes */
    if (v.kind == K_PtrVar && FIRE_UNIT_OFFSET(v) != 0) fatal("A pointer argument of an extern function points outside of its variable.");
    if (v.kind == K_PtrField) { e->field = v; cur = ptrRead(v); }
    else {
        e->target = v.kind == K_PtrVar ? (Value*)v.p : (Value*)ptrAddr(v);
        cur = *e->target;
    }
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
    Value nv;
    if (!e->target && e->field.kind != K_PtrField) return;
    switch (e->kind) {
        case K_Int: { int64_t x; memcpy(&x, e->buf.bytes, 8); nv = Int(x); break; }
        case K_Float: { double d; memcpy(&d, e->buf.bytes, 8); nv = Float((Real)d); break; }
        case K_Bool: nv = Bool(e->buf.bytes[0] != 0); break;
        case K_Char: { int32_t c; memcpy(&c, e->buf.bytes, 4); nv = Char((uint32_t)c); break; }
        default: return;
    }
    if (e->field.kind == K_PtrField) {
        /* (the number in the unit of the field: what the native function changed it to) */
        const FireFieldPtr* d = fieldPtrOf(e->field);
        if (d->hasUnit && (nv.kind == K_Int || nv.kind == K_Float)) nv.unit = d->unit;
        ptrWrite(e->field, nv);
    }
    else *e->target = nv;
}

/* ---- `Library` (docs/language/18-native-code.md, fire.Standard.LibraryMethods): a native library loaded at run time and its functions by C signature.
 * The system's part is the platform layer's (fire_plat_dlopen, fire_plat_dlsym); a call goes through the thunk the program generated for the signature - one for
 * every text of the program that is a signature (fire_libThunk, NULL for any other). The errors are thrown by the fire code of the class (the same as in the VM). ---- */
/* `release`: the library's `void(void*)` that frees a returned string after it is copied (Get's `release:`; NULL: never freed). */
typedef Value (*FireLibThunk)(void* fn, const Value* args, void* release, OwnList* list);
FIRE_HOST_EXTERN FireLibThunk fire_libThunk(const char* signature);   /* (generated: by the signature without white space) */

/* The signature without white space; 0 when it does not fit. */
FIRE_FN int fire_libSigNorm(Value text, char* out, uint32_t cap) {
    char raw[256];
    uint32_t i, n = 0;
    if (text.kind != K_String || strOf(text)->length >= sizeof raw) return 0;
    strToUtf8(text, raw, sizeof raw);
    for (i = 0; raw[i]; i++) if (raw[i] != ' ' && raw[i] != '\t' && raw[i] != '\r' && raw[i] != '\n') { if (n + 1 >= cap) return 0; out[n++] = raw[i]; }
    out[n] = 0;
    return 1;
}

/* One type of a signature at *p (the grammar of LibraryMethods.Parse): 1 a type, 2 `void` alone, 0 not a type. */
FIRE_FN int fire_libSigType(const char** p) {
    const char* s = *p;
    const char* name = s;
    size_t len;
    long width = -1;
    int depth = 0;
    while (*s >= 'a' && *s <= 'z') s++;
    len = (size_t)(s - name);
    if (*s == '[') {
        char* end;
        width = strtol(s + 1, &end, 10);
        if (end == s + 1 || *end != ']') return 0;
        s = end + 1;
    }
    while (*s == '*') { depth++; s++; }
    *p = s;
    if (len == 4 && memcmp(name, "void", 4) == 0) return width >= 0 ? 0 : depth > 0 ? 1 : 2;
    if ((len == 4 && (memcmp(name, "bool", 4) == 0 || memcmp(name, "char", 4) == 0)) || (len == 6 && memcmp(name, "string", 6) == 0)) return width < 0;
    if (len == 3 && memcmp(name, "int", 3) == 0) return width < 0 || width == 8 || width == 16 || width == 32 || width == 64;
    if (len == 5 && memcmp(name, "float", 5) == 0) return width < 0 || width == 32 || width == 64;
    return 0;
}

/* The number of parameters of a signature (without white space); -1 when it is not one. */
FIRE_FN int fire_libSigArity(const char* s) {
    int n = 0, t = fire_libSigType(&s);
    if (t == 0 || *s++ != '(') return -1;
    if (*s == ')') return s[1] ? -1 : 0;
    for (;;) {
        t = fire_libSigType(&s);
        if (t == 2 && n == 0 && *s == ')') return s[1] ? -1 : 0;   /* `int(void)` */
        if (t != 1) return -1;
        n++;
        if (*s == ',') { s++; continue; }
        if (*s == ')') return s[1] ? -1 : n;
        return -1;
    }
}

/* What Get needs to know of a valid signature (without white space; LibraryMethod.Shape): 1 the result is a pointer, 2 it is a string, 4 a parameter is a pointer. */
FIRE_FN int fire_libSigShape(const char* s) {
    const char* open = strchr(s, '(');
    int shape = 0;
    if (!open) return 0;
    if (open > s && open[-1] == '*') shape |= 1;
    else if (open - s == 6 && memcmp(s, "string", 6) == 0) shape |= 2;
    if (strchr(open, '*')) shape |= 4;
    return shape;
}

/* `__LibCall(id, a, b, c, d)` (LibraryMethods: 1 load, 2 a function's address, 3 the number of parameters of a signature, 4 a call, 5 a fire plugin's classes,
 * 6 the shape of a signature, 7 a NativeHandle's release). */
FIRE_FN Value libCall(Value id, Value a, Value b, Value c, Value d, OwnList* list) {
    char text[512];
    switch ((int)id.i) {
        case 1: {
            void* h;
            if (a.kind != K_String) return Int(0);
            strToUtf8(a, text, sizeof text);
            h = fire_plat_dlopen(text);
            if (!h) {
                /* a fire plugin by its bytecode (`kit.fireplugin`, the VM's): its library next to it */
                size_t n = strlen(text);
#if defined(_WIN32)
                const char* ext = ".dll";
#elif defined(__APPLE__)
                const char* ext = ".dylib";
#else
                const char* ext = ".so";
#endif
                size_t e = strlen(ext);
                bool plugin = n > 11 && strcmp(text + n - 11, ".fireplugin") == 0;
                if (plugin) { memcpy(text + n - 11, ext, e + 1); h = fire_plat_dlopen(text); n = n - 11 + e; }
                /* a fire plugin (its bytecode next to it) that does not load into a program that cannot load plugins: -1, Load says so */
#ifdef FIRE_MODULES
                if (!h && !g_pluginHost)
#else
                if (!h)
#endif
                {
                    if (!plugin && n > e && strcmp(text + n - e, ext) == 0 && n - e + 11 < sizeof text) {
                        FILE* f;
                        memcpy(text + n - e, ".fireplugin", 12);
                        if ((f = fopen(text, "rb")) != NULL) { fclose(f); plugin = true; }
                    }
                    if (plugin) return Int(-1);
                }
#ifdef FIRE_MODULES
                /* a fire plugin that calls a native in C of the program the program does not export (`fire_cn_<function>`): a text, Load throws it */
                if (!h) {
                    const char* why = fire_plat_dlerror();
                    const char* cn = why ? strstr(why, "fire_cn_") : NULL;
                    if (cn) {
                        char t[700];
                        size_t k = 0;
                        cn += 8;
                        while (k < 200 && ((cn[k] >= 'a' && cn[k] <= 'z') || (cn[k] >= 'A' && cn[k] <= 'Z') || (cn[k] >= '0' && cn[k] <= '9') || cn[k] == '_')) k++;
                        snprintf(t, sizeof t, "The plugin library '%s' cannot be loaded: " FIRE_PLUGIN_CNATIVE, text, (int)k, cn);
                        return newStrFromUtf8((const uint8_t*)t, strlen(t), list);
                    }
                }
#endif
            }
            return Int((int64_t)(intptr_t)h);
        }
        case 2:
            if (a.kind != K_Int || b.kind != K_String) return Int(0);
            strToUtf8(b, text, sizeof text);
            return Int((int64_t)(intptr_t)fire_plat_dlsym((void*)(intptr_t)a.i, text));
        case 3: {
            int n;
            if (!fire_libSigNorm(a, text, sizeof text) || (n = fire_libSigArity(text)) < 0) return Int(-1);
            return Int(fire_libThunk(text) ? n : -2);
        }
        case 4: {
            FireLibThunk thunk;
            Arr* args;
            int n;
            if (!fire_libSigNorm(b, text, sizeof text) || !(thunk = fire_libThunk(text))) return fireError("LibraryException", "The signature of a native function is not known to this native build.");
            n = fire_libSigArity(text);
            args = c.kind == K_Array ? arrOf(c) : NULL;
            if (!args || (int)args->length != n) {
                char t[160];
                snprintf(t, sizeof t, "The function expects %d argument(s), got %d.", n, args ? (int)args->length : 0);
                return fireError("ArgumentException", t);
            }
            return thunk((void*)(intptr_t)a.i, arrItems(args), d.kind == K_Int ? (void*)(intptr_t)d.i : NULL, list);
        }
        case 5: {
            /* a fire plugin (it exports fire_module_info): its classes registered - an array of their names; undefined: no fire plugin; a string: why it does not fit */
            const void* info = a.kind == K_Int ? fire_plat_dlsym((void*)(intptr_t)a.i, "fire_module_info") : NULL;
            if (!info) return Undef();
#ifdef FIRE_MODULES
            return fireRegisterPlugin((const FirePlugin*)info, list);
#else
            return Int(-1);   /* (this program cannot load fire plugins: not built with modules) */
#endif
        }
        case 6:
            if (!fire_libSigNorm(a, text, sizeof text) || fire_libSigArity(text) < 0) return Int(0);
            return Int(fire_libSigShape(text));
        case 7:
            /* a NativeHandle is destroyed (its destructor runs once): the library's own function frees its pointer */
            if (a.kind == K_Int && a.i != 0 && b.kind == K_Int) ((void (*)(void*))(intptr_t)a.i)((void*)(intptr_t)b.i);
            return Undef();
        default: fatal("__LibCall: unknown function");
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
 * Fire threads (docs/spec/THREADING_DESIGN.md): `fire { }` starts a real thread. All fire code runs while the thread holds the GIL (a fair ticket lock); a thread
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
extern FIRE_RT_DATA Gil g_gil;
extern FIRE_RT_DATA fire_atomic32 g_gilWaiting;

#ifdef FIRE_NO_GIL
/* (docs/ROADMAP_TASKS.md step 1: fire code runs on all cores - the hand-over points of the GIL stay the safe points, the GIL itself is gone;
 * what it protected has locks of its own. Being built step by step: until step 1g this switch is for the tests under ThreadSanitizer only.) */
FIRE_FN void gilAcquire(void) { }
FIRE_FN void gilRelease(void) { }
FIRE_FN void gilYield(void) { FIRE_PLAT_YIELD(); }
#else
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
    FIRE_PLAT_YIELD();   /* (a scheduler of fire itself: the others get their turn - a thread that has not started yet waits for nothing) */
    if (fire_atomic_load(&g_gilWaiting)) { gilRelease(); gilAcquire(); }
}
#endif

/* An event: something changed that a waiting thread may be looking for (called with the GIL held, after the change). */
extern FIRE_RT_DATA fire_mutex g_evM;
extern FIRE_RT_DATA fire_cond g_evCv;
extern FIRE_RT_DATA uint64_t g_evCount;
FIRE_FN void notifyEvent(void) {
    fire_mutex_lock(&g_evM);
    g_evCount++;
    fire_cond_notifyAll(&g_evCv);
    fire_mutex_unlock(&g_evM);
}

/* Signals. Bit 0 is for every thread (terminate), bit 1 for the main program only (thread exceptions), bit 2 for the main program too (sections and jobs that
 * wait for it; looked at only at a loop's jump back, and only in the mode `safepoints` - see g_globalJobs). */
enum { ATTN_ALL = 1, ATTN_MAIN = 2, ATTN_SECT = 4 };
extern FIRE_RT_DATA fire_atomic32 g_attn;

typedef struct TerminateState { bool requested; Value value; } TerminateState;
extern FIRE_RT_DATA TerminateState g_terminate;
extern FIRE_RT_DATA OwnList g_terminateOwn;   /* what a terminate value owns lives on until the process ends */

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
extern FIRE_RT_DATA int g_threadsLive;
extern FIRE_RT_DATA PtrVec g_threadList;     /* fire_thread* */
/* unhandled exceptions of fire threads that wait for the main program */
typedef struct ValueQueue { Value* items; uint32_t count, cap; } ValueQueue;
extern FIRE_RT_DATA ValueQueue g_pendingExc;
extern FIRE_RT_DATA PtrVec g_graveyard;      /* the global scopes of threads that ended with an unhandled exception (never freed: the exception is handed on) */
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

#ifdef FIRE_DEBUG
FIRE_RT_FN void fire_dbgThreadBegin(void);
FIRE_RT_FN void fire_dbgThreadEnd(void);
#endif
FIRE_FN void threadEntry(ThreadStart st) {
    gilAcquire();
    g_isThread = 1;
    g_attnMask = ATTN_ALL;
    g_tick = 4096;
    g_travel = st.travel;
#ifdef FIRE_DEBUG
    fire_dbgThreadBegin();
#endif
    st.fn(st.args);
#ifdef FIRE_DEBUG
    fire_dbgThreadEnd();
#endif
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
typedef struct Msg { MsgFn fn; Value* args; uint32_t n; OwnList* own; } Msg;   /* own: what the message owns (its unclaimed arguments, SPEC 2.4) */
/* A ring queue of messages. */
typedef struct Mailbox { Msg* items; uint32_t head, count, cap; } Mailbox;
extern FIRE_RT_DATA PtrMap g_mailboxes;   /* Obj* -> Mailbox* */
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
    m.own = NULL;
    if (!m.args) allocFailed();
    for (i = 0; i < n; i++) {
        Owned* o = ownedOf(args[i]);
        m.args[i] = args[i];
        retain(args[i]);
        /* an unclaimed argument (a `new`, a call result, a copy - it travels in the argument list of the call) belongs to the message until it runs */
        if (o && o->owner && o->owner->mark == AL_MARK) {
            if (!m.own) { m.own = (OwnList*)malloc(sizeof(OwnList)); if (!m.own) allocFailed(); *m.own = scopeList(0xFFFFFFFFu); }
            ownUnlink(o);
            ownLink(m.own, o);
        }
    }
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
        if (m.own) { leave(m.own); free(m.own); }
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
    /* what the message owned and the method did not keep dies with the call */
    if (m.own) { transferOut(r, m.own); leave(m.own); free(m.own); }
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
extern FIRE_RT_DATA OwnList* g_domainRoot;   /* the global scope of the main program */
FIRE_FN void markOwnedShared(Owned* c);
FIRE_FN void markArrayShared(Arr* a) {
    uint32_t i;
    if (a->h.flags & F_SHARED) return;
    a->h.flags |= F_SHARED;
    if (!arrValues(a)) return;   /* (a packed array holds numbers only) */
    for (i = 0; i < a->length; i++) if (arrItems(a)[i].kind == K_Array && ownedOf(arrItems(a)[i])) markArrayShared(arrOf(arrItems(a)[i]));
}
FIRE_FN void markOwnedShared(Owned* c) {
    if (c->okind == O_Object) {
        Obj* o = (Obj*)c;
        Owned* k;
        uint32_t i;
        if (o->h.flags & F_SHARED) return;
        o->h.flags |= F_SHARED;
        for (k = objOwnedHead(o); k; k = k->next) markOwnedShared(k);
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
extern FIRE_RT_DATA ReqQueue g_reqs;
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

/* `fire global { ... } taking x`: the job (a lambda with the copies as parameters) is queued, the caller goes on. An argument that reaches an instance of a
 * `[NoCopy]` class is the ArgumentException before anything is copied or queued (no job runs). */
FIRE_FN void postJob(Value lam, const Value* args, uint32_t n) {
    JobReq* j;
    Req q;
    uint32_t i;
    for (i = 0; i < n; i++)
        if (args[i].kind == K_Class && !noCopyFree(args[i], "fire global")) return;
    j = (JobReq*)malloc(sizeof(JobReq));
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
extern FIRE_RT_DATA PtrMap g_originOf;   /* copy -> original (null: destroyed) */
extern FIRE_RT_DATA PtrMap g_copiesOf;   /* original -> PtrVec* of its copies */
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

/* `fire ... taking x`: what the thread gets. An object, array or buffer is copied with everything reachable from it, whoever owns the parts (isolated,
 * THREADING_DESIGN 3); a copied object owned by another copied one belongs to its copy, every other copy to `travel` (the root scope of the thread). A string
 * or lambda is shared (the thread gets a count of it), other values are plain. */
FIRE_FN Value takeCopy(Value v, OwnList* travel) {
    DeepCopier c;
    Value copy;
    switch (v.kind) {
        case K_Class:
            copierInit(&c, travel);
            c.taking = true;
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

/* `fire ... taking a taking b`: every `taking` is a copy, so an instance of a `[NoCopy]` class reachable from one of them is the ArgumentException - checked
 * for all of them before the first is copied (the thread does not start). */
FIRE_FN bool takingNoCopyFree(const Value* vs, uint32_t n) {
    uint32_t i;
    for (i = 0; i < n; i++) if (!noCopyFree(vs[i], "taking")) return false;
    return true;
}

/* ---- `fire handover x`: x goes to the thread without a copy (the VM: Runtime.ObjectHandover). Everything reachable from x through fields and elements, and
 * what the objects (and arrays) own, moves as it is: a part whose owner moves too keeps it, every other part belongs to `travel` (the root scope of the thread)
 * from then on, whoever owned it before; the parts leave the shared domain of the globals. Actors and destroyed values stay shared references. */
FIRE_FN Value ownedValue(Owned* h) { return h->okind == O_Object ? ObjV((Obj*)h) : h->okind == O_Array ? ArrV((Arr*)h) : BufV((Buf*)h); }
FIRE_COLD FIRE_FN Value handoverRefused(const char* what) {
    char text[200];
    snprintf(text, sizeof text, "'handover' rejected: %s", what);
    return fireError("ArgumentException", text);
}
/* The parts that move (`seen` marks them, `parts` lists them in discovery order); false - the ArgumentException is thrown - for a lambda or a pointer in a field
 * or an element, and in a fire thread for a part of the main program's globals (the thread does not own it). Nothing is changed. */
FIRE_FN bool handoverDiscover(Value v, CopyMap* seen, Owned*** parts, uint32_t* count, uint32_t* cap) {
    Value* pending = NULL;
    uint32_t pn = 0, pcap = 0, i;
    bool ok = true;
    if (!ownedOf(v)) return true;
    pending = (Value*)fire_growPush(pending, &pn, &pcap, sizeof(Value));
    pending[pn++] = v;
    while (pn) {
        Value x = pending[--pn];
        Owned* h;
        Owned* k;
        if (x.kind == K_Lambda || isPtr(x)) {
            handoverRefused(x.kind == K_Lambda ? "lambda values cannot be handed to a thread." : "raw pointers cannot be handed to a thread.");
            ok = false;
            break;
        }
        h = ownedOf(x);
        if (!h || copyMapFind(seen, h)) continue;
        if (h->okind == O_Object && (h->flags & 8)) continue;   /* an actor stays a shared reference */
        if (g_isThread && (h->flags & F_SHARED)) {
            handoverRefused("it reaches an object of the main program's global variables, which this thread does not own.");
            ok = false;
            break;
        }
        copyMapPut(seen, h, Undef());
        *parts = (Owned**)fire_growPush(*parts, count, cap, sizeof(Owned*));
        (*parts)[(*count)++] = h;
        if (h->okind == O_Object) {
            Obj* o = (Obj*)h;
            for (i = 0; i < o->nfields; i++) {
                pending = (Value*)fire_growPush(pending, &pn, &pcap, sizeof(Value));
                pending[pn++] = objFields(o)[i];
            }
            for (k = objOwnedHead(o); k; k = k->next) {
                pending = (Value*)fire_growPush(pending, &pn, &pcap, sizeof(Value));
                pending[pn++] = ownedValue(k);
            }
        } else if (h->okind == O_Array) {
            Arr* a = (Arr*)h;
            for (i = 0; i < a->length; i++) {
                pending = (Value*)fire_growPush(pending, &pn, &pcap, sizeof(Value));
                pending[pn++] = arrItems(a)[i];
            }
            for (k = a->parts ? a->parts->head : NULL; k; k = k->next) {
                pending = (Value*)fire_growPush(pending, &pn, &pcap, sizeof(Value));
                pending[pn++] = ownedValue(k);
            }
        }
    }
    free(pending);
    return ok;
}
/* The check before anything is copied or moved (for all `handover` values of a `fire`). */
FIRE_FN bool handoverFree(const Value* vs, uint32_t n) {
    uint32_t i;
    for (i = 0; i < n; i++) {
        CopyMap seen = {0};
        Owned** parts = NULL;
        uint32_t count = 0, cap = 0;
        bool ok = handoverDiscover(vs[i], &seen, &parts, &count, &cap);
        copyMapFree(&seen);
        free(parts);
        if (!ok) return false;
    }
    return true;
}
#ifndef FIRE_UNCHECKED
/* (a reference to a moved part: the next generation - the one the part gets) */
FIRE_FN void handoverRehome(Value* v, CopyMap* seen) {
    if ((v->kind == K_Class || v->kind == K_Array || v->kind == K_Buffer) && leafAlive(*v) && copyMapFind(seen, leafOf(*v)))
        FIRE_SETGEN(*v, (FIRE_VGEN(*v) + 1) & FIRE_GEN_MASK);
}
#endif
/* What the thread gets for the `handover` values `vs` of one `fire` (all together: one may reach another): each value itself, moved (see above); a string or
 * lambda is shared as with `taking`. Every moved part gets the next generation of its slot - the references among the parts and the values the thread gets
 * carry it, every other Value (the sender's, from before the handover) is dead from then on: its use is the DestroyedException "... was handed over to a
 * thread" (destroyedError, handedOverRef), as in the VM (Runtime.ObjectHandover.MoveAll). An original with `taking` copies counts as gone for their `sync`. */
FIRE_FN void handOverAll(const Value* vs, Value* out, uint32_t n, OwnList* travel) {
    CopyMap seen = {0};
    Owned** parts = NULL;
    uint32_t count = 0, cap = 0, i;
    for (i = 0; i < n; i++) {
        out[i] = vs[i];
        if (vs[i].kind == K_String || vs[i].kind == K_Lambda) retain(vs[i]);
        else handoverDiscover(vs[i], &seen, &parts, &count, &cap);   /* (checked before: handoverFree) */
    }
    for (i = 0; i < count; i++) {
        Owned* h = parts[i];
        OwnList* l = h->owner;
        if (!(l && l->holder && copyMapFind(&seen, l->holder))) {
            if (l) ownUnlink(h);
            ownLink(travel, h);
        }
    }
    for (i = 0; i < count; i++) parts[i]->flags &= (uint8_t)~F_SHARED;
#ifndef FIRE_UNCHECKED
    for (i = 0; i < count; i++) {
        Owned* h = parts[i];
        uint32_t k;
        if (h->okind == O_Object) {
            Obj* o = (Obj*)h;
            for (k = 0; k < o->nfields; k++) handoverRehome(&objFields(o)[k], &seen);
            if (h->flags & 4) {   /* an original with `taking` copies: for them it is gone (`sync` says undefined), as in the VM */
                void** cs = ptrMapFind(&g_copiesOf, o);
                if (cs) {
                    PtrVec* copies = (PtrVec*)*cs;
                    for (k = 0; k < copies->count; k++) ptrMapPut(&g_originOf, copies->items[k], NULL);
                    free(copies->items);
                    free(copies);
                    ptrMapRemove(&g_copiesOf, o);
                }
                h->flags &= (uint8_t)~4;
            }
        } else if (h->okind == O_Array) {
            Arr* a = (Arr*)h;
            for (k = 0; k < a->length; k++) handoverRehome(&arrItems(a)[k], &seen);
        }
    }
    for (i = 0; i < n; i++) handoverRehome(&out[i], &seen);
    for (i = 0; i < count; i++) {
        slotBump(parts[i]);
    }
#endif
    copyMapFree(&seen);
    free(parts);
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
        copierInit(&c, objOwned(container));
        r = copierRun(&c, src);
        copierFree(&c);
        return r;
    }
    if (tgtObj) {
        Obj* t = asObj(tgt);
        if (objOwns(container, &t->h)) { ownUnlink(&t->h); destroy(t); }
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
        na = allocArr(sa->length, objOwned(container));
        result = ArrV(na);
        if (!arrCopyTyped(na, sa))   /* (a copy of a typed array is typed; a packed one copies its numbers) */
        for (i = 0; i < sa->length; i++) {
            Value oldElem = (oldArr && i < oldArr->length) ? arrItems(oldArr)[i] : Undef();
            arrItems(na)[i] = syncSingle(arrItems(sa)[i], oldElem, container, flat);
            retain(arrItems(na)[i]);
        }
        if (oldArr && oldArr->parts) {
            /* (an element the old array owned - a literal's, SPEC 2.1 - that stays in the new one goes over to it before the old one dies) */
            for (i = 0; i < na->length && arrValues(na); i++) {
                Owned* e = ownedOf(arrItems(na)[i]);
                if (e && e->owner == oldArr->parts) {
                    if (!na->parts) na->parts = newPartsList(&na->h);
                    ownUnlink(e);
                    ownLink(na->parts, e);
                }
            }
        }
        if (oldArr && objOwns(container, &oldArr->h)) destroyLeaf(&oldArr->h);
        return result;
    }
    if (src.kind == K_Buffer) {
        Buf* sb;
        Buf* nb;
        if (!ownedOf(src)) return destroyedError(src);
        sb = bufOf(src);
        nb = bufCopy(sb, objOwned(container));
        if (tgt.kind == K_Buffer && ownedOf(tgt) && objOwns(container, &bufOf(tgt)->h)) destroyLeaf(&bufOf(tgt)->h);
        return BufV(nb);
    }
    if (src.kind == K_Lambda || isPtr(src)) takingViolation("'sync' does not support lambda or pointer values.");
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
    if (src->nraw && src->nraw == tgt->nraw) memcpy(objRaw(tgt), objRaw(src), src->nraw);   /* (the packed fields: numbers) */
}

/* The walk of syncFields without writing anything (`[NoCopy]`, SPEC 5.12): case A of anything that reaches an instance of a `[NoCopy]` class, and case C of a
 * full sync whose source is one (its fields would go into another instance), are the ArgumentException; case C of the same instance or of `sync flat`, and
 * case B, copy nothing. The same walk as SyncEngine.RequireNoCopyFields in the VM. `seen`: the source objects visited (a cycle). */
FIRE_FN bool syncNoCopyFields(Obj* src, Obj* tgt, bool flat, CopyMap* seen);
FIRE_FN bool syncNoCopySingle(Value src, Value tgt, bool flat, CopyMap* seen) {
    uint32_t i;
    if (src.kind == K_Class && tgt.kind == K_Class) {
        Obj* s;
        if (flat || !leafAlive(src) || !leafAlive(tgt) || asObj(src) == asObj(tgt)) return true;
        s = asObj(src);
        if (FIRE_UNLIKELY(classNoCopy(s->cls))) { noCopyErrorOp(s->cls, true, "sync"); return false; }
        return syncNoCopyFields(s, asObj(tgt), false, seen);
    }
    if (src.kind == K_Class) return noCopyFree(src, "sync");
    if (tgt.kind == K_Class || src.kind != K_Array || !ownedOf(src)) return true;
    {
        Arr* sa = arrOf(src);
        Arr* oldArr = (tgt.kind == K_Array && ownedOf(tgt)) ? arrOf(tgt) : NULL;
        for (i = 0; i < sa->length; i++)
            if (!syncNoCopySingle(arrItems(sa)[i], (oldArr && i < oldArr->length) ? arrItems(oldArr)[i] : Undef(), flat, seen)) return false;
    }
    return true;
}
FIRE_FN bool syncNoCopyFields(Obj* src, Obj* tgt, bool flat, CopyMap* seen) {
    uint32_t i;
    if (copyMapFind(seen, src)) return true;
    copyMapPut(seen, src, Undef());
    for (i = 0; i < src->nfields; i++)
        if (!syncNoCopySingle(objFields(src)[i], i < tgt->nfields ? objFields(tgt)[i] : Undef(), flat, seen)) return false;
    return true;
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
    {
        CopyMap seen = {0};
        bool ok = syncNoCopyFields(copy, (Obj*)*slot, flat, &seen);
        copyMapFree(&seen);
        if (!ok) return Undef();   /* (the ArgumentException: nothing was written back) */
    }
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
extern FIRE_RT_DATA ThreadsCatch* g_threadsCatches;
extern FIRE_RT_DATA uint32_t g_threadsCatchCount, g_threadsCatchCap;
extern FIRE_RT_DATA Value (*g_terminateFn)(Value);
FIRE_FN void registerThreadsCatch(int32_t typeId, Value (*fn)(Value)) {
    g_threadsCatches = (ThreadsCatch*)fire_growPush(g_threadsCatches, &g_threadsCatchCount, &g_threadsCatchCap, sizeof(ThreadsCatch));
    g_threadsCatches[g_threadsCatchCount].typeId = typeId;
    g_threadsCatches[g_threadsCatchCount].fn = fn;
    g_threadsCatchCount++;
}
FIRE_FN void registerTerminateCatch(Value (*fn)(Value)) { g_terminateFn = fn; }

/* Host services: native code on a thread of its own (the thread of a window manager, `#import "windows"`) asks the main program to run something at its next
 * safe point - with the GIL and the real globals, like a job. Holds keep the program alive at its end (windows that are still open). */
extern FIRE_RT_DATA fire_atomic32 g_hostPending;
extern FIRE_RT_DATA fire_atomic32 g_hostHolds;   /* (a signed count in an unsigned word) */
typedef void (*HostService)(void);
extern FIRE_RT_DATA HostService* g_hostServices;
extern FIRE_RT_DATA uint32_t g_hostServiceCount, g_hostServiceCap;
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
    g_noSyncDepth++;   /* (what a service runs - a window's event handler - runs for itself: its own safe points do not serve the queue) */
    for (i = 0; i < g_hostServiceCount; i++) g_hostServices[i]();
    g_noSyncDepth--;
}

/* When the main program serves its queue by itself (docs/spec/THREADING_DESIGN.md section 7; `SetAutoSync`, the same values as fire.Runtime.GlobalJobsMode):
 * GJ_SAFEPOINTS (the default) at a loop's jump back and while waiting (`Sleep`, the wait functions), GJ_SLEEP only while waiting, GJ_SYNC never (`#nosync`);
 * `sync globals` and the end of the program always. A call (function entry) and a native call (`print`) are no safe points for it - as in the VM. */
enum { GJ_SYNC = 0, GJ_SAFEPOINTS = 1, GJ_SLEEP = 2 };
extern FIRE_RT_DATA uint8_t g_globalJobs;
/* The exceptions of the fire threads (and of the jobs) that reached the main program: `catch threads`, or the program ends. */
FIRE_FN void deliverThreadExceptions(void) {
    while (g_pendingExc.count && !g_unwind.active) {
        Value e = g_pendingExc.items[0];
        Value (*handler)(Value) = NULL;
        uint32_t i;
        memmove(g_pendingExc.items, g_pendingExc.items + 1, (g_pendingExc.count - 1) * sizeof(Value));
        g_pendingExc.count--;
        for (i = 0; i < g_threadsCatchCount; i++)
            if (g_threadsCatches[i].typeId < 0 || excMatches(e, g_threadsCatches[i].typeId)) { handler = g_threadsCatches[i].fn; break; }
        if (!handler) { reportUnhandled(e); exitNow(1); }
        g_noSyncDepth++;   /* (a handler runs for itself, as in the VM) */
        handler(e);
        g_noSyncDepth--;
    }
    if (!g_pendingExc.count) fire_atomic_and(&g_attn, ~(uint32_t)ATTN_MAIN);
}
/* The main program at a safe point: what the fire threads left for it - their exceptions, and (as the mode says: `waiting` = Sleep or a wait function, else a loop's
 * jump back) the sections, jobs and host services. */
FIRE_FN void mainPoll(bool waiting) {
    deliverThreadExceptions();
    if (!(g_globalJobs == GJ_SAFEPOINTS || (waiting && g_globalJobs == GJ_SLEEP))) return;
    /* (not inside a running job: its own safe points would run the next jobs nested, before it ends - out of order; not in a destructor, a host service, a
     * `catch threads` handler; the VM does not either) */
    if (!g_unwind.active && g_jobDepth == 0 && g_noSyncDepth == 0) { drainQueue(); hostServe(); }
}

FIRE_COLD FIRE_FN bool pollSlow(bool loop) {
    if (g_leaving) return false;
    if (g_terminate.requested) { startLeave(); return true; }
    if (g_attnMask & ATTN_MAIN) { if (loop) mainPoll(false); else deliverThreadExceptions(); }
    return g_unwind.active != 0;
}

/* A safe point for the signals (function entry, after a native function that waits): now and then the GIL is offered to the other threads, and the signals are
 * looked at - not the queue (ATTN_SECT). True when a leave/an exception is unwinding because of it. */
FIRE_FN bool pollSignals(void) {
    if (FIRE_UNLIKELY(--g_tick <= 0 || FIRE_PLAT_PREEMPTED())) { g_tick = 4096; gilYield(); }
    if (FIRE_LIKELY(!(fire_atomic_load(&g_attn) & g_attnMask & ~(uint32_t)ATTN_SECT))) return false;
    return pollSlow(false);
}

/* A loop's jump back: like pollSignals, and the main program serves its queue there (ATTN_SECT is in its mask only in the mode `safepoints`). */
FIRE_FN bool pollLoop(void) {
    if (FIRE_UNLIKELY(--g_tick <= 0 || FIRE_PLAT_PREEMPTED())) { g_tick = 4096; gilYield(); }
    if (FIRE_LIKELY(!(fire_atomic_load(&g_attn) & g_attnMask))) return false;
    return pollSlow(true);
}

/* `sync globals`: serves the queue (main program) and gives the other threads a turn; a thread gets 0. */
FIRE_FN int64_t syncGlobals(void) {
    int64_t handled = 0;
    /* (not mainPoll: its automatic processing would run the queue without counting it - `sync globals` counts what it processes, as in the VM) */
    if (!g_isThread) { deliverThreadExceptions(); handled = drainQueue(); if (!g_unwind.active) hostServe(); }
    gilYield();
    return handled;
}

/* `Sleep`: this thread sleeps - with the GIL released, so the others run. It is not deaf: `terminate` ends it at once, and the main program serves what the
 * threads leave for it (sections, jobs, their exceptions) while it sleeps; whatever that raises ends the sleep. */
FIRE_FN bool fire_sleepWake(void* ctx) {
    (void)ctx;
    if (!g_isThread) mainPoll(true);
    return g_unwind.active != 0;
}
/* A wait function of a device (or any wait that is not `Sleep`): waits with blockUntil until `pred(ctx)`, and the main program serves meanwhile, like `Sleep`. */
typedef struct FireWaitServing { bool (*pred)(void*); void* ctx; } FireWaitServing;
FIRE_FN bool fire_waitServingPred(void* c) {
    FireWaitServing* w = (FireWaitServing*)c;
    if (!g_isThread) { mainPoll(true); if (g_unwind.active) return true; }
    return w->pred(w->ctx);
}
FIRE_FN bool blockUntilServing(bool (*pred)(void*), void* ctx, bool stopOnTerminate, int64_t deadlineMs) {
    FireWaitServing w;
    w.pred = pred;
    w.ctx = ctx;
    return blockUntil(fire_waitServingPred, &w, stopOnTerminate, deadlineMs);
}
FIRE_FN void sleepTicks(int64_t ticks) {
    int32_t held;
    int64_t deadline;
    /* (`Sleep(0)` is a safe point too: the main program serves its queue once, as in the VM) */
    if (ticks <= 0) { if (!g_isThread) fire_sleepWake(NULL); gilYield(); return; }
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
    deliverThreadExceptions();
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
        g_noSyncDepth++;
        g_terminateFn(g_terminate.value);
        g_noSyncDepth--;
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
#if defined(FIRE_LIBRARY) && !defined(__cplusplus)
jmp_buf* g_libraryJump = NULL;
FireLibraryError g_libraryError;
const fire_host* g_libraryHost = NULL;
#ifndef FIRE_THREADS
HostService g_hostServices[8];
uint32_t g_hostServiceCount = 0;
fire_atomic32 g_hostPending;
#endif
#endif
#if defined(FIRE_THREADS) && defined(FIRE_DLL)
ThreadLocals* fire_tl(void) { return &fire_tlsBlock; }   /* (linkage "shared" on Windows: the block of the calling thread) */
#elif !(defined(FIRE_THREADS) && defined(FIRE_TLS_STRUCT))
#define FIRE_X(type, name, init) FIRE_TLS type FIRE_CC_NEAR_DATA name = init;
FIRE_THREAD_VARS(FIRE_X)
#undef FIRE_X
#endif
const UnitConvFn* g_unitTo = NULL;
const UnitConvFn* g_unitFrom = NULL;
UnitDef* g_ud = NULL;
uint32_t g_udCount = 0, g_udCap = 0;
bool g_dimsAdded = false;
const TextUnit* g_textUnits = NULL;
uint32_t g_textUnitCount = 0;
#ifdef FIRE_SLOTS
SlotTable g_slots;
#endif
FireFieldPtr** g_fieldPtrs;
uint32_t g_fieldPtrCount, g_fieldPtrCap;
bool g_domainOn = false;
const ResEntry* g_resources = NULL;
const FireEnum* g_enums = NULL;
uint32_t g_enumCount = 0;
int g_resourceCount = 0;
int g_argc = 0;
char** g_argv = NULL;
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
uint8_t g_globalJobs = GJ_SAFEPOINTS;
#endif
#endif  /* FIRE_RT_IMPLEMENTATION */

#ifdef FIRE_DEBUG
#include "fire_debug.h"
#endif

/* ---------------------------------------------------------------------------------------------------------------------
 * The questions the runtime asks about a class, answered from the table of class descriptors (g_classDescs, set by the program's fire_init). In the
 * translation unit with the runtime's state; a library of a package for the VM (FIRE_LIBRARY) has no classes and answers them itself (fire_pkg_wrapper.h).
 * --------------------------------------------------------------------------------------------------------------------- */
#if defined(FIRE_RT_IMPLEMENTATION) && !defined(FIRE_LIBRARY) && !defined(__cplusplus)
const FireClassDesc* g_classDescs = NULL;
uint32_t g_classDescCount = 0;
#ifdef FIRE_MODULES
const FirePluginHost* g_pluginHost = NULL;
const void** g_pluginRf = NULL;
uint32_t g_pluginRfCount = 0;
/* the catch types by name: the program's (g_excStatic of them), then those plugins brought (g_excExtra: there are some) */
static const char** g_excNames = NULL;
static uint32_t g_excCount = 0, g_excStatic = 0;
static bool g_excExtra = false;
#endif

FIRE_FN const FireClassDesc* classDescOf(uint32_t cls) { return cls < g_classDescCount ? &g_classDescs[cls] : NULL; }

const char* className(uint32_t cls) {
    const FireClassDesc* d = classDescOf(cls);
    return d ? d->name : "?";
}

void runDestructors(Obj* o) {
    const FireClassDesc* d = classDescOf(o->cls);
    /* (a destructor runs in the middle of something: the main program does not serve its queue in it, as in the VM) */
    if (d && d->destroy) { g_noSyncDepth++; d->destroy(o); g_noSyncDepth--; }
}

int32_t excMessageIndex(uint32_t cls) {
    const FireClassDesc* d = classDescOf(cls);
    return d ? d->messageIndex : -1;
}

bool excMatches(Value exception, int32_t typeId) {
    const FireClassDesc* d;
    const int32_t* t;
    if (typeId == FIRE_EXC_ANY) return true;
    d = classDescOf(asObj(exception)->cls);
#ifdef FIRE_MODULES
    /* a catch type a plugin brought (the program has no id for it): by the name of the class */
    if (d && g_excExtra && typeId >= (int32_t)g_excStatic) return typeId < (int32_t)g_excCount && fireClassIsSub(asObj(exception)->cls, g_excNames[typeId]);
#endif
    if (!d || !d->excTypes) return false;
    for (t = d->excTypes; *t >= 0; t++) if (*t == typeId) return true;
    return false;
}

bool timeObjTicks(Value v, int64_t* ticks) {
    const FireClassDesc* d = classDescOf(asObj(v)->cls);
    Value t;
    if (!d || d->ticksIndex < 0) return false;
    t = objFields(asObj(v))[d->ticksIndex];
    if (t.kind != K_Int) return false;
    *ticks = t.i;
    return true;
}

bool userToString(Value object, OwnList* list, Value* result) {
    const FireClassDesc* d = classDescOf(asObj(object)->cls);
    if (!d || !d->toString) return false;
#ifdef FIRE_DEBUG
    FIRE_DBG_NESTED(*result = d->toString(object));
#else
    *result = d->toString(object);
#endif
    adopt(*result, list);
    return true;
}

#ifdef FIRE_MODULES
/* ---- modules (ROADMAP 9): the members of an object by name - the tables of its class and its bases ---- */
static int fireMethodCmp(const char* name, uint32_t argc, const FireMethod* m) {
    int c = strcmp(name, m->name);
    return c != 0 ? c : argc < m->argc ? -1 : argc > m->argc ? 1 : 0;
}

static const FireMethod* fireMethodIn(const FireMembers* t, const char* name, int argc) {
    uint32_t lo = 0, hi = t->nmethods;
    while (lo < hi) {
        uint32_t mid = lo + (hi - lo) / 2;
        int c = fireMethodCmp(name, (uint32_t)argc, &t->methods[mid]);
        if (c == 0) return &t->methods[mid];
        if (c < 0) hi = mid; else lo = mid + 1;
    }
    return NULL;
}

/* The members plugins add to classes of the program, of its libraries and to base types (`class extends Api.Device` in a plugin, `$string` for
 * `class extends string`): a table per class and plugin, by the name of the class - an object finds them by the names of its chain, after the tables of its
 * descriptor (copied and appended when a plugin is registered: the old array stays for other threads). */
typedef struct FireExtTable { const char* cls; const FireMembers* members; } FireExtTable;
static const FireExtTable* g_extTables = NULL;
static uint32_t g_extCount = 0;

static const FireMethod* fireFindExtMethod(const char* const* isa, const char* name, int argc, uint32_t* k) {
    const FireExtTable* ext = g_extTables;
    uint32_t i, n = g_extCount;
    if (!isa || n == 0) return NULL;
    for (; *isa; isa++)
        for (i = 0; i < n; i++)
            if (strcmp(ext[i].cls, *isa) == 0) {
                const FireMethod* m = fireMethodIn(ext[i].members, name, argc);
                if (m) { *k = ext[i].members->k; return m; }
            }
    return NULL;
}

const FireMethod* fireFindMethod(uint32_t cls, const char* name, int argc, uint32_t* k) {
    const FireClassDesc* d = classDescOf(cls);
    const FireMembers* const* t;
    if (!d) return NULL;
    if (d->members)
        for (t = d->members; *t; t++) {
            const FireMethod* m = fireMethodIn(*t, name, argc);
            if (m) { *k = (*t)->k; return m; }
        }
    return fireFindExtMethod(d->isa, name, argc, k);
}

/* A method of any value by name: of the class of a living object (its tables, then what plugins add), of a base value what plugins add to its type. */
const FireMethod* fireFindValueMethod(Value self, const char* name, int argc, uint32_t* k) {
    static const char* const kStr[] = {"$string", NULL}, * const kChr[] = {"$char", NULL}, * const kInt[] = {"$int", NULL}, * const kFlt[] = {"$float", NULL},
        * const kBool[] = {"$bool", NULL}, * const kArr[] = {"$array", NULL};
    switch (self.kind) {
        case K_Class: return leafAlive(self) ? fireFindMethod(asObj(self)->cls, name, argc, k) : NULL;
        case K_String: return fireFindExtMethod(kStr, name, argc, k);
        case K_Char: return fireFindExtMethod(kChr, name, argc, k);
        case K_Int: return fireFindExtMethod(kInt, name, argc, k);
        case K_Float: return fireFindExtMethod(kFlt, name, argc, k);
        case K_Bool: return fireFindExtMethod(kBool, name, argc, k);
        case K_Array: return fireFindExtMethod(kArr, name, argc, k);
        default: return NULL;
    }
}

/* No method `name` with `argc` arguments: a class that has it with other numbers of arguments is an ArgumentException that lists them, otherwise a
 * MissingMemberException (the texts of the VM). */
Value fireNoMethod(Value self, const char* name, int argc) {
    const FireClassDesc* d;
    const FireMembers* const* t;
    uint32_t seen[16], n = 0, i, j;
    char list[160], text[500];
    if (self.kind != K_Class || !leafAlive(self)) return missingMethod(self, name, argc);
    d = classDescOf(asObj(self)->cls);
    if (d && d->members)
        for (t = d->members; *t; t++)
            for (i = 0; i < (*t)->nmethods; i++) {
                if (strcmp((*t)->methods[i].name, name) != 0) continue;
                for (j = 0; j < n && seen[j] != (*t)->methods[i].params; j++) { }
                if (j == n && n < 16) seen[n++] = (*t)->methods[i].params;
            }
    if (n == 0) return missingMethod(self, name, argc);
    for (i = 1; i < n; i++) for (j = i; j > 0 && seen[j - 1] > seen[j]; j--) { uint32_t s = seen[j]; seen[j] = seen[j - 1]; seen[j - 1] = s; }
    list[0] = 0;
    for (i = 0; i < n; i++) snprintf(list + strlen(list), sizeof list - strlen(list), i == 0 ? "%u" : ", %u", (unsigned)seen[i]);
    snprintf(text, sizeof text, "Method '%s' on '%s' has no overload with %d argument(s) (available: %s argument(s)).", name, className(asObj(self)->cls), argc, list);
    return fireError("ArgumentException", text);
}

Value fireCallMethod(Value self, const char* name, int argc, const Value* a, OwnList* list, uint32_t caller) {
    uint32_t k;
    const FireMethod* m = fireFindValueMethod(self, name, argc, &k);
    if (m) return m->fn(k, self, a, list, caller);
    return fireNoMethod(self, name, argc);
}

static const FireField* fireFindField(uint32_t cls, const char* name, uint32_t* k) {
    const FireClassDesc* d = classDescOf(cls);
    const FireMembers* const* t;
    if (!d || !d->members) return NULL;
    for (t = d->members; *t; t++) {
        uint32_t lo = 0, hi = (*t)->nfields;
        while (lo < hi) {
            uint32_t mid = lo + (hi - lo) / 2;
            int c = strcmp(name, (*t)->fields[mid].name);
            if (c == 0) { *k = (*t)->k; return &(*t)->fields[mid]; }
            if (c < 0) hi = mid; else lo = mid + 1;
        }
    }
    return NULL;
}

/* A field of an object (a real field of its class wins, otherwise its property `get_name`), by name like the VM. */
Value fireGetField(Value v, const char* name, OwnList* list, uint32_t caller) {
    uint32_t k;
    const FireField* f;
    const FireMethod* m;
    char accessor[200];
    if (v.kind != K_Class) return missingField(v, name);
    if (!leafAlive(v)) return destroyedError(v);
    f = fireFindField(asObj(v)->cls, name, &k);
    if (f) return f->get(k, v, list, caller);
    snprintf(accessor, sizeof accessor, "get_%s", name);
    m = fireFindMethod(asObj(v)->cls, accessor, 0, &k);
    if (m) { Value r = m->fn(k, v, NULL, list, caller); adopt(r, list); return r; }
    accessor[0] = 's';
    if (fireFindMethod(asObj(v)->cls, accessor, 1, &k)) {
        char text[400];
        snprintf(text, sizeof text, "Property '%s' of '%s' has no getter (only 'set').", name, className(asObj(v)->cls));
        fatal(text);
    }
    return missingField(v, name);
}

void fireSetField(Value v, Value x, const char* name, OwnList* list, uint32_t caller) {
    uint32_t k;
    const FireField* f;
    const FireMethod* m;
    char accessor[200];
    if (v.kind != K_Class) { notObjectAssign(v); return; }
    if (!leafAlive(v)) { destroyedError(v); return; }
    f = fireFindField(asObj(v)->cls, name, &k);
    if (f) { f->set(k, v, x, list, caller); return; }
    snprintf(accessor, sizeof accessor, "set_%s", name);
    m = fireFindMethod(asObj(v)->cls, accessor, 1, &k);
    if (m) { Value r = m->fn(k, v, &x, list, caller); adopt(r, list); return; }
    accessor[0] = 'g';
    if (fireFindMethod(asObj(v)->cls, accessor, 0, &k)) {
        char text[400];
        snprintf(text, sizeof text, "Property '%s' on '%s' has no setter (only 'get').", name, className(asObj(v)->cls));
        fatal(text);
    }
    missingField(v, name);
}

Value fireFieldPtr(Value v, const char* name) {
    uint32_t k;
    const FireField* f;
    char text[300];
    if (v.kind == K_Class && !leafAlive(v)) return destroyedError(v);
    f = v.kind == K_Class ? fireFindField(asObj(v)->cls, name, &k) : NULL;
    if (f && f->ptr) return f->ptr(k, v);
    snprintf(text, sizeof text, "The address of '%s' cannot be taken (it is a property or does not exist).", name);
    fatal(text);
}

/* `is of`: the class, a class of its chain or an interface of the chain is called `name`. */
bool fireIsOf(uint32_t cls, const char* name) {
    const FireClassDesc* d = classDescOf(cls);
    const char* const* n;
    if (!d) return false;
    if (d->isa) for (n = d->isa; *n; n++) if (strcmp(*n, name) == 0) return true;
    if (d->ifaces) for (n = d->ifaces; *n; n++) if (strcmp(*n, name) == 0) return true;
    return false;
}

/* The class is `name` or derived from it (the access to a protected member). */
bool fireClassIsSub(uint32_t cls, const char* name) {
    const FireClassDesc* d = classDescOf(cls);
    const char* const* n;
    if (!d || !d->isa) return false;
    for (n = d->isa; *n; n++) if (strcmp(*n, name) == 0) return true;
    return false;
}

/* The score of an object of class `cls` for a parameter of class `name` (overloads): `exact` for the class itself, one less per step to a derived class. */
int fireClassScore(uint32_t cls, const char* name, int exact, int none) {
    const FireClassDesc* d = classDescOf(cls);
    int distance = 0;
    const char* const* n;
    if (!d || !d->isa) return none;
    for (n = d->isa; *n; n++, distance++) if (strcmp(*n, name) == 0) return exact - distance;
    return none;
}

const TArg* fireTArgOf(Obj* o, const char* cls, int k) {
    const FireClassDesc* d = o ? classDescOf(o->cls) : NULL;
    return d && d->targOf ? d->targOf(o, cls, k) : NULL;
}

FIRE_RT_FN void fireProbeRawSet(Value obj, const char* name, Value value, OwnList* list, uint32_t caller) { fireSetField(obj, value, name, list, caller); }

Value fireProbeRawGet(Value obj, const char* name, OwnList* list) {
    uint32_t k;
    const FireField* f = fireFindField(asObj(obj)->cls, name, &k);
    const FireMethod* m;
    char accessor[200];
    if (f && f->index >= 0) return objFields(asObj(obj))[f->index];
    if (f && f->get) { Value r = f->get(k, obj, list, 0xFFFFFFFFu); adopt(r, list); return r; }   /* (a packed field: its place is its module's) */
    snprintf(accessor, sizeof accessor, "get_%s", name);
    m = fireFindMethod(asObj(obj)->cls, accessor, 0, &k);
    if (m) { Value r = m->fn(k, obj, NULL, list, 0xFFFFFFFFu); adopt(r, list); return r; }
    return Undef();
}

/* ---- fire plugins (see FirePlugin): the checks, then the registration of its classes, catch types, enums and reflection rows ---- */
static const FirePlugin* g_plugins[64];
static Value firePlText(const char* text, OwnList* list) { return newStrFromUtf8((const uint8_t*)text, strlen(text), list); }
static uint32_t g_pluginCount = 0;

static Value firePluginClassNames(const FirePlugin* p, OwnList* list) {
    uint32_t i, n = 0;
    Arr* names;
    for (i = 0; i < p->nclasses; i++) if (strncmp(p->classNames[i], "__", 2) != 0 && p->classNames[i][0] != '$') n++;   /* (not the classes behind its enums, nor `$int` of its `class extends int`) */
    names = allocArr(n, list);
    for (i = 0, n = 0; i < p->nclasses; i++)
        if (strncmp(p->classNames[i], "__", 2) != 0 && p->classNames[i][0] != '$') { Value s = firePlText(p->classNames[i], list); arrItems(names)[n++] = s; retain(s); }
    return ArrV(names);
}

const void* firePluginJsonAt(uint32_t cls) {
    uint32_t i, n = g_pluginCount;
    for (i = 0; i < n; i++) {
        const FirePlugin* p = g_plugins[i];
        if (p->jsonAt && cls >= *p->base && cls - *p->base < p->nclasses) return p->jsonAt(cls - *p->base);
    }
    return NULL;
}
int32_t firePluginJsonNamed(const char* name) {
    uint32_t i, n = g_pluginCount;
    for (i = 0; i < n; i++) {
        const FirePlugin* p = g_plugins[i];
        int32_t k = p->jsonNamed ? p->jsonNamed(name) : -1;
        if (k >= 0) return (int32_t)(*p->base + (uint32_t)k);
    }
    return -1;
}
const char* firePluginTArgText(const TArg* t) {
    uint32_t i, n = g_pluginCount;
    for (i = 0; i < n; i++) {
        const char* text = g_plugins[i]->jsonTargText ? g_plugins[i]->jsonTargText(t) : NULL;
        if (text) return text;
    }
    return NULL;
}
const TArg* firePluginTArgFor(const char* text) {
    uint32_t i, n = g_pluginCount;
    for (i = 0; i < n; i++) {
        const TArg* t = g_plugins[i]->jsonTargFor ? g_plugins[i]->jsonTargFor(text) : NULL;
        if (t) return t;
    }
    return NULL;
}

static Value firePluginRefused(const FirePlugin* p, const char* text, OwnList* list) {
    char t[900];
    snprintf(t, sizeof t, "The plugin '%s' cannot be loaded: %s", p->name, text);
    return firePlText(t, list);
}

/* The id of a catch type by its name: the program's, else a new one (excMatches compares it by the name of the class). */
static int32_t firePluginExcId(const char* name) {
    uint32_t i;
    const char** grown;
    for (i = 0; i < g_excCount; i++) if (strcmp(g_excNames[i], name) == 0) return (int32_t)i;
    grown = (const char**)malloc((g_excCount + 1) * sizeof *grown);
    if (!grown) fatal("out of memory");
    if (g_excCount) memcpy(grown, g_excNames, g_excCount * sizeof *grown);
    grown[g_excCount] = name;
    g_excNames = grown;   /* (the old table stays: another thread may read it) */
    g_excExtra = true;
    return (int32_t)g_excCount++;
}

/* The number of an enum by its name: the program's, else a new one. */
static uint8_t firePluginEnum(const FireEnum* e) {
    uint32_t i;
    FireEnum* grown;
    for (i = 0; i < g_enumCount; i++) if (strcmp(g_enums[i].name, e->name) == 0) return (uint8_t)(i + 1);
    if (g_enumCount >= 255) fatal("too many enums");
    grown = (FireEnum*)malloc((g_enumCount + 1) * sizeof *grown);
    if (!grown) fatal("out of memory");
    if (g_enumCount) memcpy(grown, g_enums, g_enumCount * sizeof *grown);
    grown[g_enumCount] = *e;
    g_enums = grown;
    return (uint8_t)++g_enumCount;
}

/* Its units: their dimensions by name in the program's places (new ones in free places: false when there are not enough), then each unit the program's with the
 * same dimensions, scale, symbol and offset - a unit with conversion functions only when it is not the plugin's own -, else a new one (with its functions). */
static bool firePluginDims(const FirePlugin* p, int32_t* place, bool apply) {
    uint32_t i;
    int k, next = 0;
    for (i = 0; i < p->ndims; i++) {
        place[i] = -1;
        for (k = 0; k < FIRE_NDIMS; k++) if (g_dimNames[k][0] && strcmp(g_dimNames[k], p->dimNames[i]) == 0) { place[i] = k; break; }
        if (place[i] >= 0) continue;
        while (next < FIRE_NDIMS && g_dimNames[next][0]) next++;
        if (next == FIRE_NDIMS) return false;
        place[i] = next;
        if (apply) { g_dimNames[next] = p->dimNames[i]; g_dimsAdded = true; }
        next++;
    }
    return true;
}
static void firePluginUnits(const FirePlugin* p) {
    int32_t place[FIRE_NDIMS], conv[64];
    uint32_t i, j, k, hostConv = 0;
    if (p->nunits <= 1 && p->ntext == 0) return;
    firePluginDims(p, place, true);
    for (i = 0; i < g_udCount; i++) {
        if (g_ud[i].conv >= 0 && (uint32_t)g_ud[i].conv + 1 > hostConv) hostConv = (uint32_t)g_ud[i].conv + 1;
        if (g_ud[i].ulconv >= 0 && (uint32_t)g_ud[i].ulconv + 1 > hostConv) hostConv = (uint32_t)g_ud[i].ulconv + 1;
    }
    for (k = 0; k < p->nconv && k < 64; k++) conv[k] = -1;
    p->unitMap[0] = 0;
    for (k = 1; k < p->nunits; k++) {
        const UnitInit* u = &p->units[k];
        int32_t dims[FIRE_NDIMS];
        bool own = (u->conv >= 0 && p->convOwn[u->conv]) || (u->ulconv >= 0 && p->convOwn[u->ulconv]);
        uint32_t found = 0;
        memset(dims, 0, sizeof dims);
        for (j = 0; j < p->ndims; j++) dims[place[j]] = u->dims[j];
        if (!own)
            for (i = 1; i < g_udCount && !found; i++) {
                const UnitDef* e = &g_ud[i];
                if (e->scale == u->scale && e->offset == u->offset && (e->conv >= 0) == (u->conv >= 0) && (e->ulconv >= 0) == (u->ulconv >= 0) && memcmp(e->dims, dims, sizeof dims) == 0
                    && ((e->display == NULL) == (u->display == NULL)) && (!u->display || strcmp(e->display, u->display) == 0)) found = i;
            }
        if (!found) {
            /* (a new unit: its conversion functions get the next numbers of the program) */
            int32_t c[2];
            int32_t from[2];
            int n;
            from[0] = u->conv; from[1] = u->ulconv;
            for (n = 0; n < 2; n++) {
                c[n] = -1;
                if (from[n] < 0 || (uint32_t)from[n] >= p->nconv || from[n] >= 64) continue;
                if (conv[from[n]] < 0) {
                    UnitConvFn* to = (UnitConvFn*)malloc((hostConv + 1) * sizeof *to);
                    UnitConvFn* back = (UnitConvFn*)malloc((hostConv + 1) * sizeof *back);
                    if (!to || !back) outOfMemory();
                    if (hostConv) { memcpy(to, g_unitTo, hostConv * sizeof *to); memcpy(back, g_unitFrom, hostConv * sizeof *back); }
                    to[hostConv] = p->convTo[from[n]];
                    back[hostConv] = p->convFrom[from[n]];
                    g_unitTo = to;   /* (the old tables stay: another thread may read them) */
                    g_unitFrom = back;
                    conv[from[n]] = (int32_t)hostConv++;
                }
                c[n] = conv[from[n]];
            }
            found = unitAppend(dims, u->scale, u->display, u->offset, c[0], c[1]);
        }
        p->unitMap[k] = (uint16_t)found;
    }
    /* the units a text may name: those of the plugin the program does not know by that symbol */
    if (p->ntext) {
        TextUnit* text = (TextUnit*)malloc((g_textUnitCount + p->ntext) * sizeof *text);
        uint32_t n = g_textUnitCount;
        if (!text) outOfMemory();
        if (n) memcpy(text, g_textUnits, n * sizeof *text);
        for (k = 0; k < p->ntext; k++) {
            for (i = 0; i < g_textUnitCount && strcmp(g_textUnits[i].symbol, p->textUnits[k].symbol) != 0; i++) { }
            if (i < g_textUnitCount) continue;
            text[n] = p->textUnits[k];
            text[n].unit = p->unitMap[p->textUnits[k].unit];
            n++;
        }
        g_textUnits = text;
        g_textUnitCount = n;
    }
}

Value fireRegisterPlugin(const FirePlugin* p, OwnList* list) {
    uint32_t i, j, base;
    int32_t places[FIRE_NDIMS];
    FireClassDesc* rows;
    char t[600];
    for (i = 0; i < g_pluginCount; i++) if (g_plugins[i] == p) return firePluginClassNames(p, list);   /* (loaded before) */
    if (p->format != FIRE_PLUGIN_FORMAT) return firePlText("The library is a fire plugin of another version of fire: build it again.", list);
    if (!g_pluginHost) return firePluginRefused(p, "this program cannot load fire plugins - build it with linkage \"shared\".", list);
    for (i = 0; i < p->ndeps; i++) {
        const char* has = g_pluginHost->moduleAbi(p->depNames[i]);
        if (!has) { snprintf(t, sizeof t, "it needs the module '%s', which this program does not have.", p->depNames[i]); return firePluginRefused(p, t, list); }
        if (strcmp(has, p->depAbis[i]) != 0) {
            snprintf(t, sizeof t, "it was built against another interface of the module '%s' (%.14s, the program has %.14s) - build it again against the libraries of this program.", p->depNames[i], p->depAbis[i], has);
            return firePluginRefused(p, t, list);
        }
    }
    if (strcmp(p->head, g_pluginHost->head) != 0) {
        snprintf(t, sizeof t, "it was built for another configuration of the runtime (%.12s, the program has %.12s: other switches, target or version of fire) - build it for this program (--host).", p->head, g_pluginHost->head);
        return firePluginRefused(p, t, list);
    }
    for (i = 0; i < g_pluginCount; i++)
        if (strcmp(g_plugins[i]->name, p->name) == 0) return firePluginRefused(p, "another plugin of that name is loaded already.", list);
    for (i = 0; i < p->nclasses; i++)
        for (j = 0; j < g_classDescCount && p->classNames[i][0] != '$'; j++)
            if (strcmp(g_classDescs[j].name, p->classNames[i]) == 0) { snprintf(t, sizeof t, "the program has a class '%s' already.", p->classNames[i]); return firePluginRefused(p, t, list); }
    if (g_pluginCount >= sizeof g_plugins / sizeof g_plugins[0]) return firePluginRefused(p, "too many plugins.", list);
    if (p->ndims > FIRE_NDIMS || !firePluginDims(p, places, false)) {
        snprintf(t, sizeof t, "its units need more dimensions than the program has room for (%d in all with linkage \"shared\").", FIRE_NDIMS);
        return firePluginRefused(p, t, list);
    }
    if (p->nconv > 64) return firePluginRefused(p, "it has more than 64 units with conversion functions.", list);
    /* what it adds to classes it does not declare: nothing they have (the classes by name - of the program, of libraries, of another plugin) */
    for (i = 0; i < p->nexts; i++) {
        const FireMembers* tab = p->extTables[i];
        for (j = 0; j < tab->nmethods; j++) {
            const FireMethod* m = &tab->methods[j];
            uint32_t c, k, e;
            bool has = false;
            for (c = 0; c < g_classDescCount && !has; c++)
                if (strcmp(g_classDescs[c].name, p->extClasses[i]) == 0) has = fireFindMethod(c, m->name, (int)m->argc, &k) != NULL;
            for (e = 0; e < g_extCount && !has; e++) has = strcmp(g_extTables[e].cls, p->extClasses[i]) == 0 && fireMethodIn(g_extTables[e].members, m->name, (int)m->argc) != NULL;
            if (has) {
                snprintf(t, sizeof t, "it adds the method '%s' (%u arguments) to '%s', which has it already (another plugin added it, or the class of the program has it).",
                    m->name, (unsigned)m->argc, p->extClasses[i][0] == '$' ? p->extClasses[i] + 1 : p->extClasses[i]);
                return firePluginRefused(p, t, list);
            }
        }
    }
    if (p->link) {
        const char* missing = g_pluginHost->find ? p->link(g_pluginHost->find) : "fire_hostFind";
        if (missing && strncmp(missing, "fire_cn_", 8) == 0) { snprintf(t, sizeof t, FIRE_PLUGIN_CNATIVE, (int)strlen(missing + 8), missing + 8); return firePluginRefused(p, t, list); }
        if (missing) { snprintf(t, sizeof t, "it needs '%s', which the program does not have.", missing); return firePluginRefused(p, t, list); }
    }
    /* its units: the program's numbers (new ones for those it does not have) */
    firePluginUnits(p);
    if (!g_excNames) {
        g_excNames = (const char**)g_pluginHost->excNames;
        g_excCount = g_excStatic = g_pluginHost->nexc;
    }
    /* the catch types and enums of its code: the program's numbers (new ones for those it does not have) */
    for (i = 0; i < p->nexc; i++) p->excMap[i] = firePluginExcId(p->excNames[i]);
    p->enumMap[0] = 0;
    for (i = 0; i < p->nenums; i++) p->enumMap[i + 1] = firePluginEnum(&p->enums[i]);
    /* its classes: the next ids */
    base = g_classDescCount;
    rows = (FireClassDesc*)calloc(base + p->nclasses + 1, sizeof *rows);
    if (!rows) fatal("out of memory");
    if (base) memcpy(rows, g_classDescs, base * sizeof *rows);
    *p->base = base;
    p->fillClasses(rows + base);
    for (i = 0; i < p->nclasses; i++) {
        /* the catch types of the program an object of the class is (by the names of its chain) */
        int32_t* types = (int32_t*)malloc((g_excStatic + 1) * sizeof *types);
        uint32_t n = 0;
        const char* const* c;
        if (!types) fatal("out of memory");
        for (j = 1; j < g_excStatic; j++)
            for (c = rows[base + i].isa; c && *c; c++) if (strcmp(*c, g_excNames[j]) == 0) { types[n++] = (int32_t)j; break; }
        types[n] = -1;
        rows[base + i].excTypes = types;
    }
    g_classDescs = rows;   /* (the old table stays: another thread may read it) */
    g_classDescCount = base + p->nclasses;
    if (p->rfRows) {
        const void** rf = (const void**)malloc((g_pluginRfCount + p->nclasses) * sizeof *rf);
        if (!rf) fatal("out of memory");
        if (g_pluginRfCount) memcpy(rf, g_pluginRf, g_pluginRfCount * sizeof *rf);
        for (i = 0; i < p->nclasses; i++) rf[g_pluginRfCount + i] = p->rfRows[i];
        g_pluginRf = rf;
        g_pluginRfCount += p->nclasses;
    }
    if (p->nexts) {
        FireExtTable* ext = (FireExtTable*)malloc((g_extCount + p->nexts) * sizeof *ext);
        if (!ext) fatal("out of memory");
        if (g_extCount) memcpy(ext, g_extTables, g_extCount * sizeof *ext);
        for (i = 0; i < p->nexts; i++) { ext[g_extCount + i].cls = p->extClasses[i]; ext[g_extCount + i].members = p->extTables[i]; }
        g_extTables = ext;   /* (the old array stays: another thread may read it) */
        g_extCount += p->nexts;
    }
    g_plugins[g_pluginCount++] = p;
#ifdef FIRE_DEBUG
    fire_dbgPlugin(p);   /* (its sources get their indexes and the debugger hears of them before its first line runs) */
#endif
    if (p->init) p->init();
    return firePluginClassNames(p, list);
}
#endif
#endif


/* The generated code passes a Value it only reads by its address (a variable or a slot): on a 16-bit machine a Value by value is a copy of 16 bytes at every call site;
 * where the compiler inlines these the code is the same as with the Value. */
FIRE_FN bool leafAliveP(const Value* v) { return leafAlive(*v); }
FIRE_FN Value destroyedErrorP(const Value* v) { return destroyedError(*v); }
FIRE_FN void retainP(const Value* v) { retain(*v); }
FIRE_FN void releaseP(const Value* v) { release(*v); }
FIRE_FN bool truthyP(const Value* v) { return truthy(*v); }
FIRE_FN Obj* objAtP(const Value* v) { return objAt(*v); }
FIRE_FN void adoptP(const Value* v, OwnList* list) { adopt(*v, list); }
FIRE_FN void ctorEndP(const Value* o) { ctorEnd(*o); }
FIRE_FN void hoistFromP(const Value* v, OwnList* from, OwnList* to) { hoistFrom(*v, from, to); }
FIRE_FN void transferTreeP(const Value* v, OwnList* const* lists, int n) { transferTree(*v, lists, n); }
FIRE_FN void finishArgsP(const Value* result, OwnList* al) { finishArgs(*result, al); }
FIRE_FN void claimToListP(const Value* v, OwnList* from, OwnList* to) { claimToList(*v, from, to); }

FIRE_END

FIRE_CC_RUNTIME_END

#endif  /* FIRE_RT_H */
