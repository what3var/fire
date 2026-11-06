/* fire native runtime - the dialect of the C compiler (docs/spec/NATIVE_BACKEND.md, "Compiler"). Everything the runtime needs that C does not say the same way in
 * every compiler is defined in one small file per compiler family; this file only picks it. A new compiler is a new file here and one line below.
 *
 * Each file defines:
 *   FIRE_COLD                 a function that is rarely called (not inlined)
 *   FIRE_LIKELY(x) / FIRE_UNLIKELY(x)
 *   FIRE_UNUSED_LABEL         a label that may be unused
 *   FIRE_MAYBE_UNUSED         a function or variable that may be unused
 *   FIRE_NORETURN             a function that never returns
 *   FIRE_NOT_REACHED(r)       after a call of a FIRE_NORETURN function where the compiler cannot see that it does not return: `return r;` there, else nothing
 *   FIRE_CONST_FN             a function whose result depends only on its arguments
 *   FIRE_TLS_KEYWORD          thread-local storage of the compiler (empty: none - the platform keeps the state of a thread, FIRE_TLS_STRUCT)
 *   FIRE_STATIC_ASSERT_C(c,m) a check when compiling (C; C++ has static_assert)
 *   FIRE_CC_RUNTIME_BEGIN / FIRE_CC_RUNTIME_END   around the runtime: what the compiler should not warn about there
 *   FIRE_FAR / FIRE_HUGE      the segmented pointers of 16-bit x86 (empty elsewhere)
 *   FIRE_CC_FEQ(x, y)         optional: `==` of two floating-point numbers that is false with a NaN (the 8087 code of Open Watcom says "equal")
 *   FIRE_CC_FIXED(d, places)  optional: the number given to printf("%.*f") (where the C library rounds a half the other way)
 *   FIRE_CC_BIG_ALLOC(bytes) / FIRE_CC_BIG_FREE(p)   optional: a block larger than a size_t says (16-bit x86, the huge model); FREE: 1 if it was one of them
 *   FIRE_CC_ZERO(p, bytes) / FIRE_CC_COPY(to, from, bytes)   optional: memset/memcpy of a block of 32-bit size (where the C library's wrap at a segment end)
 *   FIRE_CC_IO_STEP(p, n)     optional: how many of the n bytes at p one call of fread/fwrite/memcpy may take (16-bit x86: up to the end of the segment)
 *   FIRE_CC_NAME              the name of the family ("gcc", "clang", "msvc", "watcom", "tcc", "generic") */
#ifndef FIRE_CC_H
#define FIRE_CC_H

#if defined(__WATCOMC__)
#include "fire_cc_watcom.h"
#elif defined(__clang__) || defined(__GNUC__)
#include "fire_cc_gcc.h"
#elif defined(_MSC_VER)
#include "fire_cc_msvc.h"
#else
#include "fire_cc_generic.h"   /* TinyCC and any other C99 compiler */
#endif

#ifndef FIRE_FAR
#define FIRE_FAR
#endif
/* `==` of two floating-point numbers (false with a NaN; a compiler whose comparison says "equal" for NaN checks first) */
#ifndef FIRE_CC_FEQ
#define FIRE_CC_FEQ(x, y) ((x) == (y))
#endif
/* A number before printf("%.*f", places, d) (a C library that does not round a half to the even digit moves it first) */
#ifndef FIRE_CC_FIXED
#define FIRE_CC_FIXED(d, places) (d)
#endif
#ifndef FIRE_HUGE
#define FIRE_HUGE
#endif
/* The bytes of a block one call of the C library may take at once (an int64_t count: the result fits a size_t) */
#ifndef FIRE_CC_IO_STEP
#define FIRE_CC_IO_STEP(p, n) ((size_t)(n))
#endif

#endif
