/* fire graphics: sizes and memory. The pixels of a framebuffer, an image or a glyph cache can be larger than what a size_t of the 16-bit real mode says
 * (64 KB - a palette framebuffer of 320x200 is 64000 bytes, an RGBA one 256000): the library counts pixels and bytes in fire_gfx_size, which is size_t
 * where that has at least 32 bits and uint32_t on dos16, and takes its blocks through these functions - on dos16 the blocks of the runtime (dataAlloc:
 * halloc above 64 KB; copied, moved and filled piece by piece up to the end of a segment), elsewhere the C library as it is. */
#ifndef FIRE_GFX_MEM_H
#define FIRE_GFX_MEM_H
#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#if SIZE_MAX < 0xFFFFFFFFu
#define FIRE_GFX_SMALL_SIZE 1
typedef uint32_t fire_gfx_size;
#else
typedef size_t fire_gfx_size;
#endif
/* The integers of the library (coordinates, colours, fixed point, the fields of image files): at least 32 bits - an `int` of the 16-bit real mode has 16,
 * so there it is an int32_t; elsewhere the C int as it is. (`int` stays only where the C library wants one: qsort's comparison, printf's %d.) */
#if INT_MAX < 0x7FFFFFFF
typedef int32_t fire_gfx_int;
#else
typedef int fire_gfx_int;
#endif
static inline fire_gfx_int fire_gfx_iabs(fire_gfx_int v) { return v < 0 ? -v : v; }

#ifdef FIRE_GFX_SMALL_SIZE
/* (one block the C library can take at once from p: up to the end of the segment, at most 32 KB) */
static size_t fire_gfx_step(const void* p, const void* q, fire_gfx_size n) {
    size_t a = FIRE_CC_IO_STEP(p, (int64_t)n), b = FIRE_CC_IO_STEP(q, (int64_t)n);
    return a < b ? a : b;
}
static void* fire_gfx_malloc(fire_gfx_size n) { return dataAlloc(0, n ? n : 1, 1); }
static void* fire_gfx_calloc(fire_gfx_size count, fire_gfx_size elem) {
    uint64_t n = (uint64_t)count * elem;
    void* p;
    if (n > 0x7FFFFFF0u) return NULL;
    p = dataAlloc(0, (uint32_t)(n ? n : 1), 1);
    if (p) dataZero(p, (uint32_t)n);
    return p;
}
static void fire_gfx_free(void* p) { if (p) dataFree(p); }
static void* fire_gfx_realloc(void* p, fire_gfx_size oldBytes, fire_gfx_size newBytes) {
    void* q;
    if (p == NULL) return fire_gfx_malloc(newBytes);
    if (oldBytes <= FIRE_CC_BIG_FROM && newBytes <= FIRE_CC_BIG_FROM) {
        q = realloc(p, (size_t)newBytes);   /* (both small: blocks of the C library - dataAlloc takes the larger ones elsewhere) */
        if (q) return q;
    }
    q = fire_gfx_malloc(newBytes);
    if (q == NULL) return NULL;
    dataCopy(q, p, oldBytes < newBytes ? oldBytes : newBytes);
    dataFree(p);
    return q;
}
static void fire_gfx_copy(void* to, const void* from, fire_gfx_size n) { dataCopy(to, from, n); }
static void fire_gfx_set(void* p, int v, fire_gfx_size n) {
    uint8_t* q = (uint8_t*)p;
    while (n > 0) { size_t step = fire_gfx_step(q, q, n); memset(q, v, step); q += step; n -= step; }
}
/* overlapping blocks: forwards when the target lies before the source, else from the end */
static void fire_gfx_move(void* to, const void* from, fire_gfx_size n) {
    uint8_t* d = (uint8_t*)to;
    const uint8_t* s = (const uint8_t*)from;
    if (n == 0 || d == s) return;
    if (d < s) {
        while (n > 0) { size_t step = fire_gfx_step(d, s, n); memmove(d, s, step); d += step; s += step; n -= step; }
    } else {
        d += n; s += n;
        while (n > 0) {
            size_t step = n > 0x4000u ? 0x4000u : (size_t)n;   /* (backwards: a step of 16 KB never crosses two segment ends; memmove takes it within one) */
            d -= step; s -= step;
            if (FIRE_CC_IO_STEP(d, step) < step || FIRE_CC_IO_STEP(s, step) < step) { size_t i; for (i = step; i > 0; i--) d[i - 1] = s[i - 1]; }
            else memmove(d, s, step);
            n -= step;
        }
    }
}
#else
#define fire_gfx_malloc(n) malloc(n)
#define fire_gfx_calloc(count, elem) calloc((count), (elem))
#define fire_gfx_free(p) free(p)
#define fire_gfx_realloc(p, oldBytes, newBytes) realloc((p), (newBytes))
#define fire_gfx_copy(to, from, n) memcpy((to), (from), (n))
#define fire_gfx_set(p, v, n) memset((p), (v), (n))
#define fire_gfx_move(to, from, n) memmove((to), (from), (n))
#endif

#endif
