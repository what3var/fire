/* fire native graphics: framebuffers (RGBA or palette), the palette, drawing (lines, rectangles, circles, ellipses, triangles, polygons, flood fill, text), blitting.
 * A port of src/fire.Terminal (Framebuffer, Palette, Paint, Surface, Shapes, Brush, Pen, Blitter, Renderer) - the same pixels come out. No operating system, no exceptions: it runs on
 * a microcontroller; a platform package only has to put the pixels on a screen (see ../../windows/native/fire_bridge_windows.h).
 *
 * A color is a "paint": a number 0..255 is an index into the palette of the framebuffer, anything else is a direct value r + g*256 + b*65536 + a*16777216.
 *
 * C99. Everything is `static` (the file is part of the generated program); the functions and types are prefixed fire_gfx_ / FireGfx. */
#ifndef FIRE_GFX_H
#define FIRE_GFX_H

#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "fire_gfx_font.h"
#include "fire_gfx_mem.h"

enum { FIRE_GFX_RGBA = 0, FIRE_GFX_INDEXED = 1 };

static uint32_t fire_gfx_rgb(uint32_t r, uint32_t g, uint32_t b) { return r | (g << 8) | (b << 16) | 0xFF000000u; }

/* the small helpers of the C++ standard library, with its exact semantics (std::min(a, b) is `b < a ? b : a`, std::max(a, b) is `a < b ? b : a`) */
static fire_gfx_int fire_gfx_imin(fire_gfx_int a, fire_gfx_int b) { return b < a ? b : a; }
static fire_gfx_int fire_gfx_imax(fire_gfx_int a, fire_gfx_int b) { return a < b ? b : a; }
static int64_t fire_gfx_lmin(int64_t a, int64_t b) { return b < a ? b : a; }
static int64_t fire_gfx_lmax(int64_t a, int64_t b) { return a < b ? b : a; }
static double fire_gfx_dmin(double a, double b) { return b < a ? b : a; }
static double fire_gfx_dmax(double a, double b) { return a < b ? b : a; }

typedef struct FireGfxPaint {
    uint32_t rgba;
    int16_t index;   /* -1: a direct value */
} FireGfxPaint;
static fire_gfx_int fire_gfx_paintIsIndex(FireGfxPaint p) { return p.index >= 0; }
static FireGfxPaint fire_gfx_paintIndex(uint8_t i) { FireGfxPaint p; p.rgba = 0; p.index = i; return p; }
static FireGfxPaint fire_gfx_paintRgba(uint32_t packed) { FireGfxPaint p; p.rgba = packed; p.index = -1; return p; }
/* An argument of a script: a number without bits above the lowest byte is a palette index. */
static FireGfxPaint fire_gfx_paintArgument(int64_t value) {
    uint32_t raw = (uint32_t)(int32_t)value;
    return (raw & 0xFFFFFF00u) == 0 ? fire_gfx_paintIndex((uint8_t)raw) : fire_gfx_paintRgba(raw);
}

/* A color resolved for a target (see fire_gfx_resolve): `rgba` is the value (its alpha counts when blending), `index` the palette entry for a palette framebuffer. */
typedef struct FireGfxPixel { uint32_t rgba; uint8_t index; } FireGfxPixel;
static FireGfxPixel fire_gfx_pixelNone(void) { FireGfxPixel p; p.rgba = 0; p.index = 0; return p; }

typedef struct FireGfxPalette {
    uint32_t entries[256];
    fire_gfx_int version;
} FireGfxPalette;

/* The 16 colors of QBasic, the 6x6x6 color cube, 24 grays. */
static void fire_gfx_paletteDefaults(FireGfxPalette* p) {
    static const fire_gfx_int levels[6] = {0, 51, 102, 153, 204, 255};
    uint32_t qbasic[16];
    fire_gfx_int i, r, g, b, idx = 16;
    qbasic[0] = fire_gfx_rgb(0, 0, 0); qbasic[1] = fire_gfx_rgb(0, 0, 170); qbasic[2] = fire_gfx_rgb(0, 170, 0); qbasic[3] = fire_gfx_rgb(0, 170, 170);
    qbasic[4] = fire_gfx_rgb(170, 0, 0); qbasic[5] = fire_gfx_rgb(170, 0, 170); qbasic[6] = fire_gfx_rgb(170, 85, 0); qbasic[7] = fire_gfx_rgb(170, 170, 170);
    qbasic[8] = fire_gfx_rgb(85, 85, 85); qbasic[9] = fire_gfx_rgb(85, 85, 255); qbasic[10] = fire_gfx_rgb(85, 255, 85); qbasic[11] = fire_gfx_rgb(85, 255, 255);
    qbasic[12] = fire_gfx_rgb(255, 85, 85); qbasic[13] = fire_gfx_rgb(255, 85, 255); qbasic[14] = fire_gfx_rgb(255, 255, 85); qbasic[15] = fire_gfx_rgb(255, 255, 255);
    for (i = 0; i < 16; i++) p->entries[i] = qbasic[i];
    for (r = 0; r < 6; r++) for (g = 0; g < 6; g++) for (b = 0; b < 6; b++) p->entries[idx++] = fire_gfx_rgb((uint32_t)levels[r], (uint32_t)levels[g], (uint32_t)levels[b]);
    for (i = 0; i < 24; i++) { uint32_t v = (uint32_t)(8 + i * 10); p->entries[232 + i] = fire_gfx_rgb(v, v, v); }
}
static void fire_gfx_paletteInit(FireGfxPalette* p) { p->version = 0; fire_gfx_paletteDefaults(p); }
static void fire_gfx_paletteSet(FireGfxPalette* p, uint8_t i, uint32_t color) { p->entries[i] = color; p->version++; }
static uint32_t fire_gfx_paletteGet(const FireGfxPalette* p, uint8_t i) { return p->entries[i]; }
static void fire_gfx_paletteSetAll(FireGfxPalette* p, const uint32_t* colors, fire_gfx_int n) {
    fire_gfx_int i;
    if (n > 256) n = 256;
    for (i = 0; i < n; i++) p->entries[i] = colors[i];
    p->version++;
}
/* The entry that is closest to a color (the squared distance in R, G, B; the first one wins). */
static uint8_t fire_gfx_paletteNearest(const FireGfxPalette* p, uint32_t color) {
    fire_gfx_int r = (fire_gfx_int)(color & 0xFF), g = (fire_gfx_int)((color >> 8) & 0xFF), b = (fire_gfx_int)((color >> 16) & 0xFF);
    fire_gfx_int best = 0, bestDistance = 0x7FFFFFFF, i;
    for (i = 0; i < 256; i++) {
        uint32_t e = p->entries[i];
        fire_gfx_int dr = (fire_gfx_int)(e & 0xFF) - r, dg = (fire_gfx_int)((e >> 8) & 0xFF) - g, db = (fire_gfx_int)((e >> 16) & 0xFF) - b;
        fire_gfx_int distance = dr * dr + dg * dg + db * db;
        if (distance < bestDistance) {
            bestDistance = distance;
            best = i;
            if (distance == 0) break;
        }
    }
    return (uint8_t)best;
}
static fire_gfx_int fire_gfx_paletteSame(const FireGfxPalette* a, const FireGfxPalette* b) { return a == b || memcmp(a->entries, b->entries, sizeof a->entries) == 0; }

typedef struct FireGfxFramebuffer {
    fire_gfx_int width, height, mode;
    uint32_t* pixels;   /* RGBA: width*height */
    uint8_t* indices;   /* palette mode: width*height */
    FireGfxPalette palette;
    fire_gfx_int transparentIndex;
    fire_gfx_int refs;   /* the table of the bridge, every canvas and every window that shows it: it lives as long as one of them does */
    void (*presentFn)(void);   /* a presenter of a package that the windows showing it call instead (a fire_present_fn, fire_display_c.h; scene3d DirectDrawing), or NULL */
    void* presentCtx;
    void* gpu;   /* its twin on the GPU (fire_gfx_gpu.h), or NULL */
} FireGfxFramebuffer;

/* The GPU backend (fire_gfx_gpu.h) is asked before the CPU reads (`writes` 0) or draws (1) the pixels of a framebuffer that has a twin there - it fetches them back
 * when the GPU drew since - and when a framebuffer goes. NULL without it. */
static void (*g_fireGfxCpuHook)(FireGfxFramebuffer* fb, fire_gfx_int writes);
static void (*g_fireGfxFreeHook)(FireGfxFramebuffer* fb);
static void fire_gfx_fbCpu(const FireGfxFramebuffer* fb, fire_gfx_int writes) { if (fb->gpu != NULL && g_fireGfxCpuHook != NULL) g_fireGfxCpuHook((FireGfxFramebuffer*)fb, writes); }

#define FIRE_GFX_MAX_SIDE ((int64_t)16384)
#define FIRE_GFX_MAX_PIXELS ((int64_t)64 * 1024 * 1024)

/* An empty framebuffer (no pixels yet, the default palette, one reference) in place. */
static void fire_gfx_fbInit(FireGfxFramebuffer* fb) {
    fb->width = 0; fb->height = 0; fb->mode = FIRE_GFX_RGBA;
    fb->pixels = NULL; fb->indices = NULL;
    fire_gfx_paletteInit(&fb->palette);
    fb->transparentIndex = -1;
    fb->refs = 1;
    fb->presentFn = NULL; fb->presentCtx = NULL;
    fb->gpu = NULL;
}
/* A new empty framebuffer (malloc), NULL without memory. */
static FireGfxFramebuffer* fire_gfx_fbNew(void) {
    FireGfxFramebuffer* fb = (FireGfxFramebuffer*)fire_gfx_malloc(sizeof(FireGfxFramebuffer));
    if (fb != NULL) fire_gfx_fbInit(fb);
    return fb;
}
static void fire_gfx_fbFreePixels(FireGfxFramebuffer* fb) { fire_gfx_free(fb->pixels); fire_gfx_free(fb->indices); fb->pixels = NULL; fb->indices = NULL; }
static void fire_gfx_fbDelete(FireGfxFramebuffer* fb) { if (fb == NULL) return; if (fb->gpu != NULL && g_fireGfxFreeHook != NULL) g_fireGfxFreeHook(fb); fire_gfx_fbFreePixels(fb); fire_gfx_free(fb); }

/* Allocates the pixels; 0 when the size is invalid or does not fit in memory. */
static fire_gfx_int fire_gfx_fbCreate(FireGfxFramebuffer* fb, int64_t w, int64_t h, fire_gfx_int m) {
    fire_gfx_size n;
    if (w <= 0 || h <= 0 || w > 0x7FFFFFFF || h > 0x7FFFFFFF || w * h > 0x7FFFFFFF || (m != FIRE_GFX_RGBA && m != FIRE_GFX_INDEXED)) return 0;
    fb->width = (fire_gfx_int)w; fb->height = (fire_gfx_int)h; fb->mode = m;
    n = (fire_gfx_size)(w * h);
    if (m == FIRE_GFX_INDEXED) { fb->indices = (uint8_t*)fire_gfx_calloc(n, 1); return fb->indices != NULL; }
    fb->pixels = (uint32_t*)fire_gfx_calloc(n, 4);
    return fb->pixels != NULL;
}

static fire_gfx_int fire_gfx_validSize(int64_t w, int64_t h) { return w >= 1 && h >= 1 && w <= FIRE_GFX_MAX_SIDE && h <= FIRE_GFX_MAX_SIDE && w * h <= FIRE_GFX_MAX_PIXELS; }

/* A new size (see fire_gfx_validSize; an invalid size leaves it as it is, 0): the content stays at the upper left, the new area is transparent (RGBA) or index 0. The pixel arrays are new ones. */
static fire_gfx_int fire_gfx_fbResize(FireGfxFramebuffer* fb, int64_t w, int64_t h) {
    fire_gfx_size n;
    fire_gfx_int copyW, copyH, y;
    uint8_t* newIndices = NULL;
    uint32_t* newPixels = NULL;
    if (!fire_gfx_validSize(w, h)) return 0;
    if (w == fb->width && h == fb->height) return 1;
    fire_gfx_fbCpu(fb, 1);
    n = (fire_gfx_size)(w * h);
    copyW = (fire_gfx_int)fire_gfx_lmin(w, fb->width); copyH = (fire_gfx_int)fire_gfx_lmin(h, fb->height);
    if (fb->indices) { newIndices = (uint8_t*)fire_gfx_calloc(n, 1); if (!newIndices) return 0; }
    else { newPixels = (uint32_t*)fire_gfx_calloc(n, 4); if (!newPixels) return 0; }
    for (y = 0; y < copyH; y++) {
        if (fb->indices) fire_gfx_copy(newIndices + (fire_gfx_size)y * (fire_gfx_size)w, fb->indices + (fire_gfx_size)y * (fire_gfx_size)fb->width, (fire_gfx_size)copyW);
        else fire_gfx_copy(newPixels + (fire_gfx_size)y * (fire_gfx_size)w, fb->pixels + (fire_gfx_size)y * (fire_gfx_size)fb->width, (fire_gfx_size)copyW * 4);
    }
    fire_gfx_free(fb->pixels); fire_gfx_free(fb->indices);
    fb->pixels = newPixels; fb->indices = newIndices;
    fb->width = (fire_gfx_int)w; fb->height = (fire_gfx_int)h;
    return 1;
}

static fire_gfx_int fire_gfx_fbIsIndexed(const FireGfxFramebuffer* fb) { return fb->indices != NULL; }
static fire_gfx_size fire_gfx_fbPixelCount(const FireGfxFramebuffer* fb) { return (fire_gfx_size)fb->width * (fire_gfx_size)fb->height; }
static fire_gfx_size fire_gfx_fbByteCount(const FireGfxFramebuffer* fb) { return fb->indices ? fire_gfx_fbPixelCount(fb) : fire_gfx_fbPixelCount(fb) * 4; }

/* The pixels as colors, also for a palette framebuffer (for a window that shows the framebuffer). */
static void fire_gfx_fbResolveTo(const FireGfxFramebuffer* fb, uint32_t* out) {
    fire_gfx_size i, n = fire_gfx_fbPixelCount(fb);
    fire_gfx_fbCpu(fb, 0);
    if (!fb->indices) { if (n) fire_gfx_copy(out, fb->pixels, n * 4); return; }
    for (i = 0; i < n; i++) out[i] = fb->palette.entries[fb->indices[i]];
}

static uint32_t fire_gfx_fbGetRaw(const FireGfxFramebuffer* fb, fire_gfx_int x, fire_gfx_int y) {
    fire_gfx_size i;
    fire_gfx_fbCpu(fb, 0);
    if ((uint32_t)x >= (uint32_t)fb->width || (uint32_t)y >= (uint32_t)fb->height) return 0;
    i = (fire_gfx_size)y * fb->width + x;
    return fb->indices ? fb->indices[i] : fb->pixels[i];
}
static uint8_t fire_gfx_fbGetIndex(const FireGfxFramebuffer* fb, fire_gfx_int x, fire_gfx_int y) {
    fire_gfx_size i;
    fire_gfx_fbCpu(fb, 0);
    if ((uint32_t)x >= (uint32_t)fb->width || (uint32_t)y >= (uint32_t)fb->height) return 0;
    i = (fire_gfx_size)y * fb->width + x;
    return fb->indices ? fb->indices[i] : fire_gfx_paletteNearest(&fb->palette, fb->pixels[i]);
}
static uint32_t fire_gfx_fbGetPixel(const FireGfxFramebuffer* fb, fire_gfx_int x, fire_gfx_int y) {
    fire_gfx_size i;
    fire_gfx_fbCpu(fb, 0);
    if ((uint32_t)x >= (uint32_t)fb->width || (uint32_t)y >= (uint32_t)fb->height) return 0;
    i = (fire_gfx_size)y * fb->width + x;
    return fb->indices ? fire_gfx_paletteGet(&fb->palette, fb->indices[i]) : fb->pixels[i];
}

static void fire_gfx_fbRetain(FireGfxFramebuffer* fb) { fb->refs++; }
static void fire_gfx_fbRelease(FireGfxFramebuffer* fb) { if (--fb->refs == 0) fire_gfx_fbDelete(fb); }

/* (the decision of fire_gfx_makeMask for one color) */
static uint8_t fire_gfx_maskDecide(uint32_t packed, uint8_t threshold, fire_gfx_int darkIsRemoved, uint8_t alphaThreshold) {
    double lum;
    fire_gfx_int dark;
    if ((packed >> 24) < alphaThreshold) return 0;
    lum = 0.299 * (packed & 0xFF) + 0.587 * ((packed >> 8) & 0xFF) + 0.114 * ((packed >> 16) & 0xFF);
    dark = lum < threshold;
    return (darkIsRemoved ? dark : !dark) ? 1 : 0;
}
/* A palette framebuffer of the same size that marks the pixels to cut out: index 1 (white) = set, index 0 (black) = not (see the slicer). */
static fire_gfx_int fire_gfx_makeMask(const FireGfxFramebuffer* src, uint8_t threshold, fire_gfx_int darkIsRemoved, uint8_t alphaThreshold, FireGfxFramebuffer* mask) {
    fire_gfx_size i, n;
    fire_gfx_fbCpu(src, 0);
    if (!fire_gfx_fbCreate(mask, src->width, src->height, FIRE_GFX_INDEXED)) return 0;
    fire_gfx_paletteSet(&mask->palette, 0, fire_gfx_rgb(0, 0, 0));
    fire_gfx_paletteSet(&mask->palette, 1, fire_gfx_rgb(255, 255, 255));
    mask->transparentIndex = 0;
    n = fire_gfx_fbPixelCount(src);
    if (src->indices) {
        uint8_t table[256];
        fire_gfx_int k;
        for (k = 0; k < 256; k++) table[k] = fire_gfx_maskDecide(fire_gfx_paletteGet(&src->palette, (uint8_t)k), threshold, darkIsRemoved, alphaThreshold);
        for (i = 0; i < n; i++) mask->indices[i] = table[src->indices[i]];
    } else
        for (i = 0; i < n; i++) mask->indices[i] = fire_gfx_maskDecide(src->pixels[i], threshold, darkIsRemoved, alphaThreshold);
    return 1;
}

/* ---- the surface: pixels and spans with clipping and alpha blending ------------------------------------------------------------------------------------ */
/* Mixes `src` (alpha a) over `dst`: dst*(255-a)/255 + src*a/255 per channel; the result is opaque when one of them was. */
static uint32_t fire_gfx_mix(uint32_t dst, uint32_t src) {
    uint32_t a = src >> 24, inv, r, g, b, outA;
    if (a == 255 || (dst >> 24) == 0) return src;
    if (a == 0) return dst;
    inv = 255 - a;
    r = ((src & 0xFF) * a + (dst & 0xFF) * inv + 127) / 255;
    g = (((src >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv + 127) / 255;
    b = (((src >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv + 127) / 255;
    outA = a + ((dst >> 24) * inv + 127) / 255;
    return r | (g << 8) | (b << 16) | ((outA < 255u ? outA : 255u) << 24);
}

/* x / 255 for 0 <= x <= 65152 (what a channel times alpha plus the rounding can be): in each 16-bit lane of `x`. */
#define FIRE_GFX_DIV255_2(x) ((((x) + 0x00010001u + (((x) >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu)

/* Mixes `src` over each of the `n` pixels - the same result as fire_gfx_mix for every pixel, red and blue in one multiplication. */
static void fire_gfx_blendRow(uint32_t* p, fire_gfx_size n, uint32_t src) {
    uint32_t a = src >> 24, inv, rb, g;
    fire_gfx_size k;
    if (a == 0) { for (k = 0; k < n; k++) if ((p[k] >> 24) == 0) p[k] = src; return; }   /* (as fire_gfx_mix: over nothing it is the color) */
    if (a == 255) { for (k = 0; k < n; k++) p[k] = src; return; }
    inv = 255 - a;
    rb = (src & 0x00FF00FFu) * a + 0x007F007Fu;
    g = ((src >> 8) & 0xFFu) * a + 127u;
    for (k = 0; k < n; k++) {
        uint32_t d = p[k], da = d >> 24, x, y, outA;
        if (da == 0) { p[k] = src; continue; }
        x = (d & 0x00FF00FFu) * inv + rb;
        y = ((d >> 8) & 0xFFu) * inv + g;
        outA = a + (da * inv + 127u) / 255u;
        p[k] = FIRE_GFX_DIV255_2(x) | (((y + 1u + (y >> 8)) >> 8) << 8) | ((outA < 255u ? outA : 255u) << 24);
    }
}

/* What the drawing functions write through: the pixels of a framebuffer with clipping at the edge and - when `blend` - alpha blending. A color with alpha: in an RGBA target
 * with blending alpha 255 is a copy, 0 draws nothing, in between it is mixed; without blending it is copied with its alpha. A palette target only copies: with blending a color
 * from alpha 128 on, below that nothing; without blending always. */
typedef struct FireGfxSurface {
    FireGfxFramebuffer* fb;
    uint32_t* pixels;
    uint8_t* indices;
    fire_gfx_int width, height;
    fire_gfx_int blend;
    fire_gfx_int clipLeft, clipTop, clipRight, clipBottom;   /* drawing is limited to x in [clipLeft, clipRight) and y in [clipTop, clipBottom), inside the target */
} FireGfxSurface;

static FireGfxSurface fire_gfx_surface(FireGfxFramebuffer* f, fire_gfx_int b, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb) {
    FireGfxSurface s;
    fire_gfx_fbCpu(f, 1);   /* (the CPU draws: the pixels are fetched from the GPU first, where it drew since) */
    s.fb = f; s.pixels = f->pixels; s.indices = f->indices; s.width = f->width; s.height = f->height; s.blend = b;
    s.clipLeft = fire_gfx_imax(0, cl); s.clipTop = fire_gfx_imax(0, ct); s.clipRight = fire_gfx_imin(f->width, cr); s.clipBottom = fire_gfx_imin(f->height, cb);
    return s;
}

static fire_gfx_int fire_gfx_surfaceIsIndexed(const FireGfxSurface* s) { return s->indices != NULL; }

static FireGfxPixel fire_gfx_resolve(const FireGfxSurface* s, FireGfxPaint paint) {
    FireGfxPixel p;
    const FireGfxPalette* palette = &s->fb->palette;
    if (s->indices) {
        uint8_t index = fire_gfx_paintIsIndex(paint) ? (uint8_t)paint.index : fire_gfx_paletteNearest(palette, paint.rgba);
        p.rgba = fire_gfx_paintIsIndex(paint) ? fire_gfx_paletteGet(palette, index) : paint.rgba;
        p.index = index;
        return p;
    }
    if (fire_gfx_paintIsIndex(paint)) { p.rgba = fire_gfx_paletteGet(palette, (uint8_t)paint.index); p.index = (uint8_t)paint.index; return p; }
    p.rgba = paint.rgba; p.index = 0;
    return p;
}

static fire_gfx_int fire_gfx_visible(const FireGfxSurface* s, const FireGfxPixel* p) {
    uint32_t a;
    if (!s->blend) return 1;
    a = p->rgba >> 24;
    return s->indices ? a >= 128 : a != 0;
}
static fire_gfx_int fire_gfx_isCopy(const FireGfxSurface* s, const FireGfxPixel* p) { return !s->blend || s->indices || (p->rgba >> 24) == 255; }

static void fire_gfx_put(FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y, const FireGfxPixel* p) {
    fire_gfx_size i;
    if (x < s->clipLeft || x >= s->clipRight || y < s->clipTop || y >= s->clipBottom) return;
    if (!fire_gfx_visible(s, p)) return;
    i = (fire_gfx_size)y * s->width + x;
    if (s->indices) s->indices[i] = p->index;
    else if (!s->blend || (p->rgba >> 24) == 255) s->pixels[i] = p->rgba;
    else s->pixels[i] = fire_gfx_mix(s->pixels[i], p->rgba);
}

/* A horizontal line from x0 to x1 (both included, any order) in row y, clipped. */
static void fire_gfx_span(FireGfxSurface* s, fire_gfx_int y, fire_gfx_int x0, fire_gfx_int x1, const FireGfxPixel* p) {
    fire_gfx_size at, n, k;
    if (y < s->clipTop || y >= s->clipBottom) return;
    if (x1 < x0) { fire_gfx_int t = x0; x0 = x1; x1 = t; }
    x0 = fire_gfx_imax(s->clipLeft, x0);
    x1 = fire_gfx_imin(s->clipRight - 1, x1);
    if (x1 < x0 || !fire_gfx_visible(s, p)) return;
    at = (fire_gfx_size)y * s->width + x0; n = (fire_gfx_size)(x1 - x0 + 1);
    if (s->indices) fire_gfx_set(s->indices + at, p->index, n);
    else if (!s->blend || (p->rgba >> 24) == 255) for (k = 0; k < n; k++) s->pixels[at + k] = p->rgba;
    else fire_gfx_blendRow(s->pixels + at, n, p->rgba);
}

static void fire_gfx_rect(FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, const FireGfxPixel* p) {
    fire_gfx_int y0, y1, xr, yy;
    if (w <= 0 || h <= 0) return;
    y0 = fire_gfx_imax(s->clipTop, y); y1 = (fire_gfx_int)fire_gfx_lmin(s->clipBottom, (int64_t)y + h);
    xr = (fire_gfx_int)fire_gfx_lmin((int64_t)x + w - 1, 0x7FFFFFFF);
    for (yy = y0; yy < y1; yy++) fire_gfx_span(s, yy, x, xr, p);
}

/* The raw value of the pixel: the index in a palette target, else the color; 0 outside. */
static uint32_t fire_gfx_raw(const FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y) {
    fire_gfx_size i;
    if ((uint32_t)x >= (uint32_t)s->width || (uint32_t)y >= (uint32_t)s->height) return 0;
    i = (fire_gfx_size)y * s->width + x;
    return s->indices ? s->indices[i] : s->pixels[i];
}

/* ---- shapes: they hand pixels and spans to a sink (a brush fills them, a pen stamps them) --------------------------------------------------------------------- */
typedef struct FireGfxSink FireGfxSink;
struct FireGfxSink {
    void (*point)(FireGfxSink* self, fire_gfx_int x, fire_gfx_int y);
    void (*span)(FireGfxSink* self, fire_gfx_int y, fire_gfx_int x0, fire_gfx_int x1);
};

static void fire_gfx_shapeLine(FireGfxSink* sink, fire_gfx_int x0, fire_gfx_int y0, fire_gfx_int x1, fire_gfx_int y1) {
    int64_t dxl = llabs((int64_t)x1 - x0), dyl = llabs((int64_t)y1 - y0);
    fire_gfx_int dx, sx, dy, sy, err;
    if (y0 == y1) { sink->span(sink, y0, x0, x1); return; }
    if (dxl > 0x7FFFFFFF / 2 || dyl > 0x7FFFFFFF / 2) return;   /* absurd coordinates: draw nothing instead of overflowing */
    dx = (fire_gfx_int)dxl; sx = x0 < x1 ? 1 : -1;
    dy = -(fire_gfx_int)dyl; sy = y0 < y1 ? 1 : -1;
    err = dx + dy;
    for (;;) {
        fire_gfx_int e2;
        sink->point(sink, x0, y0);
        if (x0 == x1 && y0 == y1) break;
        e2 = 2 * err;
        if (e2 >= dy) { err += dy; x0 += sx; }
        if (e2 <= dx) { err += dx; y0 += sy; }
    }
}

static void fire_gfx_shapeRect(FireGfxSink* sink, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h) {
    fire_gfx_int yy;
    if (w <= 0 || h <= 0) return;
    sink->span(sink, y, x, x + w - 1);
    if (h > 1) sink->span(sink, y + h - 1, x, x + w - 1);
    for (yy = y + 1; yy < y + h - 1; yy++) {
        sink->point(sink, x, yy);
        if (w > 1) sink->point(sink, x + w - 1, yy);
    }
}

#define FIRE_GFX_MAX_RADIUS (1 << 14)

/* The half widths of the rows of an ellipse: the pixels (dx, dy) with (2dx)^2/a^2 + (2dy)^2/b^2 <= 1, a = 2rx+1, b = 2ry+1. malloc'd (ry + 1 entries), NULL without memory. */
static fire_gfx_int* fire_gfx_ellipseSpans(fire_gfx_int rx, fire_gfx_int ry) {
    int64_t a = 2LL * rx + 1, b = 2LL * ry + 1;
    int64_t a2 = a * a, b2 = b * b;
    fire_gfx_int* spans = (fire_gfx_int*)fire_gfx_malloc(sizeof(fire_gfx_int) * ((fire_gfx_size)ry + 1));
    fire_gfx_int dy;
    if (spans == NULL) return NULL;
    for (dy = 0; dy <= ry; dy++) {
        int64_t rhs = a2 * (b2 - 4LL * dy * dy);
        int64_t dx = (int64_t)sqrt((double)rhs / (4.0 * (double)b2));
        while (dx > 0 && 4 * dx * dx * b2 > rhs) dx--;
        while (4 * (dx + 1) * (dx + 1) * b2 <= rhs) dx++;
        spans[(fire_gfx_size)dy] = (fire_gfx_int)fire_gfx_lmin(dx, rx);
    }
    return spans;
}

static void fire_gfx_shapeEllipse(FireGfxSink* sink, fire_gfx_int cx, fire_gfx_int cy, fire_gfx_int rx, fire_gfx_int ry) {
    fire_gfx_int* spans;
    fire_gfx_int dy;
    if (rx < 0 || ry < 0) return;
    rx = fire_gfx_imin(rx, FIRE_GFX_MAX_RADIUS);
    ry = fire_gfx_imin(ry, FIRE_GFX_MAX_RADIUS);
    spans = fire_gfx_ellipseSpans(rx, ry);
    if (spans == NULL) return;
    for (dy = 0; dy <= ry; dy++) {
        fire_gfx_int dx = spans[(fire_gfx_size)dy];
        fire_gfx_int neighbor = dy < ry ? spans[(fire_gfx_size)dy + 1] : -1;
        fire_gfx_int from, sign, rowSign;
        if (dy == 0) neighbor = ry >= 1 ? fire_gfx_imin(neighbor, spans[1]) : -1;
        from = fire_gfx_imin(neighbor + 1, dx);
        for (sign = -1; sign <= 1; sign += 2)
            for (rowSign = -1; rowSign <= 1; rowSign += 2) {
                fire_gfx_int y;
                if (dy == 0 && rowSign == 1) continue;
                y = cy + rowSign * dy;
                sink->span(sink, y, cx + sign * from, cx + sign * dx);
            }
    }
    fire_gfx_free(spans);
}

static void fire_gfx_shapeFillEllipse(FireGfxSink* sink, fire_gfx_int cx, fire_gfx_int cy, fire_gfx_int rx, fire_gfx_int ry) {
    fire_gfx_int* spans;
    fire_gfx_int dy;
    if (rx < 0 || ry < 0) return;
    rx = fire_gfx_imin(rx, FIRE_GFX_MAX_RADIUS);
    ry = fire_gfx_imin(ry, FIRE_GFX_MAX_RADIUS);
    spans = fire_gfx_ellipseSpans(rx, ry);
    if (spans == NULL) return;
    for (dy = 0; dy <= ry; dy++) {
        sink->span(sink, cy + dy, cx - spans[(fire_gfx_size)dy], cx + spans[(fire_gfx_size)dy]);
        if (dy != 0) sink->span(sink, cy - dy, cx - spans[(fire_gfx_size)dy], cx + spans[(fire_gfx_size)dy]);
    }
    fire_gfx_free(spans);
}

static void fire_gfx_shapeTriangle(FireGfxSink* sink, fire_gfx_int x0, fire_gfx_int y0, fire_gfx_int x1, fire_gfx_int y1, fire_gfx_int x2, fire_gfx_int y2) {
    fire_gfx_shapeLine(sink, x0, y0, x1, y1);
    fire_gfx_shapeLine(sink, x1, y1, x2, y2);
    fire_gfx_shapeLine(sink, x2, y2, x0, y0);
}

/* `points`: x0, y0, x1, y1, ... (`count` numbers). */
static void fire_gfx_shapePolygon(FireGfxSink* sink, const fire_gfx_int* points, fire_gfx_size count, fire_gfx_int closed) {
    fire_gfx_int n = (fire_gfx_int)(count / 2), i;
    if (n < 2) return;
    for (i = 0; i + 1 < n; i++) fire_gfx_shapeLine(sink, points[2 * i], points[2 * i + 1], points[2 * i + 2], points[2 * i + 3]);
    if (closed && n > 2) fire_gfx_shapeLine(sink, points[2 * (n - 1)], points[2 * (n - 1) + 1], points[0], points[1]);
}

static int fire_gfx_byInt(const void* a, const void* b) { fire_gfx_int x = *(const fire_gfx_int*)a, y = *(const fire_gfx_int*)b; return (x > y) - (x < y); }   /* (qsort wants an int) */

/* Even-odd fill: the rows between the crossings of the edges (half open, so a vertex counts once), then the outline. */
static void fire_gfx_shapeFillPolygon(FireGfxSink* sink, const fire_gfx_int* points, fire_gfx_size count, fire_gfx_int clipHeight) {
    fire_gfx_int n = (fire_gfx_int)(count / 2), i, y, yFrom, yTo;
    int64_t minY = INT64_MAX, maxY = INT64_MIN;
    fire_gfx_int* crossings;
    if (n < 3) { fire_gfx_shapePolygon(sink, points, count, 0); return; }
    for (i = 0; i < n; i++) { minY = fire_gfx_lmin(minY, points[2 * i + 1]); maxY = fire_gfx_lmax(maxY, points[2 * i + 1]); }
    yFrom = (fire_gfx_int)fire_gfx_lmax(minY, 0); yTo = (fire_gfx_int)fire_gfx_lmin(maxY, (int64_t)clipHeight - 1);
    crossings = (fire_gfx_int*)fire_gfx_malloc(sizeof(fire_gfx_int) * (fire_gfx_size)n);
    if (crossings == NULL) return;
    for (y = yFrom; y <= yTo; y++) {
        fire_gfx_size c = 0, k;
        for (i = 0; i < n; i++) {
            fire_gfx_int ax = points[2 * i], ay = points[2 * i + 1];
            fire_gfx_int bx = points[2 * ((i + 1) % n)], by = points[2 * ((i + 1) % n) + 1];
            int64_t num, den, x;
            if (ay == by) continue;
            if (ay > by) { fire_gfx_int t = ax; ax = bx; bx = t; t = ay; ay = by; by = t; }
            if (y < ay || y >= by) continue;
            num = (int64_t)(bx - ax) * (y - ay);
            den = by - ay;
            x = ax + (num >= 0 ? (2 * num + den) / (2 * den) : -((2 * -num + den) / (2 * den)));
            crossings[c++] = (fire_gfx_int)fire_gfx_lmax(fire_gfx_lmin(x, 0x7FFFFFFF / 2), INT32_MIN / 2);
        }
        qsort(crossings, c, sizeof(fire_gfx_int), fire_gfx_byInt);
        for (k = 0; k + 1 < c; k += 2) sink->span(sink, y, crossings[k], crossings[k + 1]);
    }
    fire_gfx_free(crossings);
    fire_gfx_shapePolygon(sink, points, count, 1);   /* the outline belongs to the area (and closes gaps left by the rounding) */
}

static void fire_gfx_shapeFillTriangle(FireGfxSink* sink, fire_gfx_int x0, fire_gfx_int y0, fire_gfx_int x1, fire_gfx_int y1, fire_gfx_int x2, fire_gfx_int y2, fire_gfx_int clipHeight) {
    fire_gfx_int points[6];
    points[0] = x0; points[1] = y0; points[2] = x1; points[3] = y1; points[4] = x2; points[5] = y2;
    fire_gfx_shapeFillPolygon(sink, points, 6, clipHeight);
}

/* ---- brush: how an area is filled ------------------------------------------------------------------------------------------------------------------------ */
/* A solid brush. It offers the fill functions (rectangle, circle, ellipse, triangle, polygon, flood fill); the renderer only hands it the surface. */
typedef struct FireGfxBrush { FireGfxPaint color; } FireGfxBrush;

typedef struct FireGfxFillSink {
    FireGfxSink base;
    FireGfxSurface* surface;
    FireGfxPixel pixel;
} FireGfxFillSink;
static void fire_gfx_fillPoint(FireGfxSink* self, fire_gfx_int x, fire_gfx_int y) { FireGfxFillSink* k = (FireGfxFillSink*)self; fire_gfx_put(k->surface, x, y, &k->pixel); }
static void fire_gfx_fillSpan(FireGfxSink* self, fire_gfx_int y, fire_gfx_int x0, fire_gfx_int x1) { FireGfxFillSink* k = (FireGfxFillSink*)self; fire_gfx_span(k->surface, y, x0, x1, &k->pixel); }
static FireGfxFillSink fire_gfx_fillSink(FireGfxSurface* s, FireGfxPixel pixel) {
    FireGfxFillSink k;
    k.base.point = fire_gfx_fillPoint; k.base.span = fire_gfx_fillSpan;
    k.surface = s; k.pixel = pixel;
    return k;
}

static void fire_gfx_brushFillRect(const FireGfxBrush* b, FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h) {
    FireGfxFillSink k;
    fire_gfx_int y0, y1, xr, yy;
    if (w <= 0 || h <= 0) return;
    k = fire_gfx_fillSink(s, fire_gfx_resolve(s, b->color));
    y0 = fire_gfx_imax(s->clipTop, y); y1 = (fire_gfx_int)fire_gfx_lmin(s->clipBottom, (int64_t)y + h);
    xr = (fire_gfx_int)fire_gfx_lmin((int64_t)x + w - 1, 0x7FFFFFFF);
    for (yy = y0; yy < y1; yy++) k.base.span(&k.base, yy, x, xr);
}
static void fire_gfx_brushFillCircle(const FireGfxBrush* b, FireGfxSurface* s, fire_gfx_int cx, fire_gfx_int cy, fire_gfx_int r) { FireGfxFillSink k = fire_gfx_fillSink(s, fire_gfx_resolve(s, b->color)); fire_gfx_shapeFillEllipse(&k.base, cx, cy, r, r); }
static void fire_gfx_brushFillEllipse(const FireGfxBrush* b, FireGfxSurface* s, fire_gfx_int cx, fire_gfx_int cy, fire_gfx_int rx, fire_gfx_int ry) { FireGfxFillSink k = fire_gfx_fillSink(s, fire_gfx_resolve(s, b->color)); fire_gfx_shapeFillEllipse(&k.base, cx, cy, rx, ry); }
static void fire_gfx_brushFillTriangle(const FireGfxBrush* b, FireGfxSurface* s, fire_gfx_int x0, fire_gfx_int y0, fire_gfx_int x1, fire_gfx_int y1, fire_gfx_int x2, fire_gfx_int y2) {
    FireGfxFillSink k = fire_gfx_fillSink(s, fire_gfx_resolve(s, b->color));
    fire_gfx_shapeFillTriangle(&k.base, x0, y0, x1, y1, x2, y2, s->height);
}
static void fire_gfx_brushFillPolygon(const FireGfxBrush* b, FireGfxSurface* s, const fire_gfx_int* points, fire_gfx_size count) {
    FireGfxFillSink k = fire_gfx_fillSink(s, fire_gfx_resolve(s, b->color));
    fire_gfx_shapeFillPolygon(&k.base, points, count, s->height);
}

/* (the flood fill) */
typedef struct FireGfxRun { fire_gfx_int y, x0, x1; } FireGfxRun;
typedef struct FireGfxFlood {
    FireGfxSurface* s;
    uint8_t* seen;
    fire_gfx_int w, hasBorder, copy;
    uint32_t borderRaw, fill, target;
} FireGfxFlood;
static fire_gfx_int fire_gfx_fillable(const FireGfxFlood* f, fire_gfx_int px, fire_gfx_int py) {
    uint32_t v;
    if (f->seen[(fire_gfx_size)py * f->w + px]) return 0;
    v = fire_gfx_raw(f->s, px, py);
    return f->hasBorder ? (v != f->borderRaw && !(f->copy && v == f->fill)) : v == f->target;
}
/* Appends to a growing array (`*items` of `*count`, room for `*cap`): 0 without memory. */
static fire_gfx_int fire_gfx_grow(void** items, fire_gfx_size* cap, fire_gfx_size count, fire_gfx_size size) {
    fire_gfx_size n;
    void* grown;
    if (count < *cap) return 1;
    n = *cap ? *cap * 2 : 64;
    grown = fire_gfx_realloc(*items, *cap * size, n * size);
    if (grown == NULL) return 0;
    *items = grown;
    *cap = n;
    return 1;
}

/* Fills the 4-connected area around (x, y) that has the pixel value of the start; with a border: everything except border and fill color. The area is determined
 * first (the pixels stay untouched), then filled - so mixing a half transparent color does not change what belongs to the area. */
static void fire_gfx_brushFlood(const FireGfxBrush* b, FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y, fire_gfx_int hasBorder, FireGfxPixel border) {
    fire_gfx_int indexed, h;
    FireGfxPixel fillPixel;
    FireGfxFlood f;
    FireGfxRun* runs = NULL;
    fire_gfx_size runCount = 0, runCap = 0, k;
    fire_gfx_int* stack = NULL;   /* pairs x, y */
    fire_gfx_size stackCount = 0, stackCap = 0;
    FireGfxFillSink sink;
    if ((uint32_t)x >= (uint32_t)s->width || (uint32_t)y >= (uint32_t)s->height) return;
    indexed = fire_gfx_surfaceIsIndexed(s);
    fillPixel = fire_gfx_resolve(s, b->color);
    f.s = s;
    f.fill = indexed ? fillPixel.index : fillPixel.rgba;
    f.target = fire_gfx_raw(s, x, y);
    f.borderRaw = indexed ? border.index : border.rgba;
    f.copy = fire_gfx_isCopy(s, &fillPixel);
    f.hasBorder = hasBorder;

    if (!hasBorder && f.target == f.fill && f.copy) return;
    if (hasBorder && (f.target == f.borderRaw || (f.target == f.fill && f.copy))) return;

    f.w = s->width; h = s->height;
    f.seen = (uint8_t*)fire_gfx_calloc((fire_gfx_size)f.w * h, 1);
    if (f.seen == NULL) return;

    if (!fire_gfx_grow((void**)&stack, &stackCap, stackCount, sizeof(fire_gfx_int) * 2)) goto done;
    stack[2 * stackCount] = x; stack[2 * stackCount + 1] = y; stackCount++;
    while (stackCount > 0) {
        fire_gfx_int sx, sy, left, right, px, ny;
        stackCount--;
        sx = stack[2 * stackCount]; sy = stack[2 * stackCount + 1];
        if (!fire_gfx_fillable(&f, sx, sy)) continue;
        left = sx; right = sx;
        while (left > 0 && fire_gfx_fillable(&f, left - 1, sy)) left--;
        while (right < f.w - 1 && fire_gfx_fillable(&f, right + 1, sy)) right++;
        for (px = left; px <= right; px++) f.seen[(fire_gfx_size)sy * f.w + px] = 1;
        if (!fire_gfx_grow((void**)&runs, &runCap, runCount, sizeof(FireGfxRun))) goto done;
        runs[runCount].y = sy; runs[runCount].x0 = left; runs[runCount].x1 = right; runCount++;
        for (ny = sy - 1; ny <= sy + 1; ny += 2) {
            fire_gfx_int inRun = 0;
            if ((uint32_t)ny >= (uint32_t)h) continue;
            for (px = left; px <= right; px++) {
                fire_gfx_int ok = fire_gfx_fillable(&f, px, ny);
                if (ok && !inRun) {
                    if (!fire_gfx_grow((void**)&stack, &stackCap, stackCount, sizeof(fire_gfx_int) * 2)) goto done;
                    stack[2 * stackCount] = px; stack[2 * stackCount + 1] = ny; stackCount++;
                }
                inRun = ok;
            }
        }
    }
    sink = fire_gfx_fillSink(s, fillPixel);
    for (k = 0; k < runCount; k++) sink.base.span(&sink.base, runs[k].y, runs[k].x0, runs[k].x1);
done:
    fire_gfx_free(f.seen);
    fire_gfx_free(runs);
    fire_gfx_free(stack);
}
static void fire_gfx_brushFloodFill(const FireGfxBrush* b, FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y) { fire_gfx_brushFlood(b, s, x, y, 0, fire_gfx_pixelNone()); }
static void fire_gfx_brushFloodFillBorder(const FireGfxBrush* b, FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y, FireGfxPaint border) { fire_gfx_brushFlood(b, s, x, y, 1, fire_gfx_resolve(s, border)); }

/* ---- pen: points, lines, paths --------------------------------------------------------------------------------------------------------------------------- */
enum { FIRE_GFX_PEN_ROUND = 0, FIRE_GFX_PEN_SQUARE = 1 };
#define FIRE_GFX_PEN_MAX_WIDTH 512

/* A pen draws with a tip of `width` pixels. The tip is rendered once (the rows of its stamp) and copied to every pixel of a line - mixed when the color is half transparent
 * (the union of all stamps is mixed once, so overlaps do not mix twice). */
typedef struct FireGfxStampRow { fire_gfx_int dy, dx0, dx1; } FireGfxStampRow;
typedef struct FireGfxPen {
    FireGfxPaint color;
    fire_gfx_int width;
    fire_gfx_int shape;
    FireGfxStampRow stamp[FIRE_GFX_PEN_MAX_WIDTH];
    fire_gfx_int stampCount;
} FireGfxPen;

static void fire_gfx_penRender(FireGfxPen* pen) {
    fire_gfx_int w = pen->width, half = (w - 1) / 2, i, j;
    pen->stampCount = 0;
    for (j = 0; j < w; j++) {
        fire_gfx_int first = -1, last = -1;
        for (i = 0; i < w; i++) {
            fire_gfx_int inside = pen->shape == FIRE_GFX_PEN_SQUARE
                || (int64_t)(2 * i - (w - 1)) * (2 * i - (w - 1)) + (int64_t)(2 * j - (w - 1)) * (2 * j - (w - 1)) <= (int64_t)w * w - 1;
            if (!inside) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first >= 0) {
            FireGfxStampRow* r = &pen->stamp[pen->stampCount++];
            r->dy = j - half; r->dx0 = first - half; r->dx1 = last - half;
        }
    }
}
static void fire_gfx_penInit(FireGfxPen* pen, FireGfxPaint c, fire_gfx_int w, fire_gfx_int s) {
    pen->color = c;
    pen->width = fire_gfx_imax(1, fire_gfx_imin(w, FIRE_GFX_PEN_MAX_WIDTH));
    pen->shape = s;
    fire_gfx_penRender(pen);
}
static void fire_gfx_penSetWidth(FireGfxPen* pen, fire_gfx_int w) { pen->width = fire_gfx_imax(1, fire_gfx_imin(w, FIRE_GFX_PEN_MAX_WIDTH)); fire_gfx_penRender(pen); }
static void fire_gfx_penSetShape(FireGfxPen* pen, fire_gfx_int s) { pen->shape = s; fire_gfx_penRender(pen); }

typedef struct FireGfxStampSink {
    FireGfxSink base;
    FireGfxSurface* surface;
    FireGfxPixel pixel;
    const FireGfxPen* pen;
} FireGfxStampSink;
static void fire_gfx_stampPoint(FireGfxSink* self, fire_gfx_int x, fire_gfx_int y) {
    FireGfxStampSink* k = (FireGfxStampSink*)self;
    fire_gfx_int i;
    for (i = 0; i < k->pen->stampCount; i++) { const FireGfxStampRow* r = &k->pen->stamp[i]; fire_gfx_span(k->surface, y + r->dy, x + r->dx0, x + r->dx1, &k->pixel); }
}
static void fire_gfx_stampSpan(FireGfxSink* self, fire_gfx_int y, fire_gfx_int x0, fire_gfx_int x1) {
    FireGfxStampSink* k = (FireGfxStampSink*)self;
    fire_gfx_int i;
    if (x1 < x0) { fire_gfx_int t = x0; x0 = x1; x1 = t; }
    for (i = 0; i < k->pen->stampCount; i++) { const FireGfxStampRow* r = &k->pen->stamp[i]; fire_gfx_span(k->surface, y + r->dy, x0 + r->dx0, x1 + r->dx1, &k->pixel); }
}

typedef struct FireGfxUnionSink {
    FireGfxSink base;
    FireGfxSurface* surface;
    FireGfxPixel pixel;
    const FireGfxPen* pen;
    FireGfxRun* runs;
    fire_gfx_size count, cap;
} FireGfxUnionSink;
static void fire_gfx_unionAdd(FireGfxUnionSink* k, fire_gfx_int y, fire_gfx_int x0, fire_gfx_int x1) {
    if (!fire_gfx_grow((void**)&k->runs, &k->cap, k->count, sizeof(FireGfxRun))) return;
    k->runs[k->count].y = y; k->runs[k->count].x0 = x0; k->runs[k->count].x1 = x1; k->count++;
}
static void fire_gfx_unionPoint(FireGfxSink* self, fire_gfx_int x, fire_gfx_int y) {
    FireGfxUnionSink* k = (FireGfxUnionSink*)self;
    fire_gfx_int i;
    for (i = 0; i < k->pen->stampCount; i++) { const FireGfxStampRow* r = &k->pen->stamp[i]; fire_gfx_unionAdd(k, y + r->dy, x + r->dx0, x + r->dx1); }
}
static void fire_gfx_unionSpan(FireGfxSink* self, fire_gfx_int y, fire_gfx_int x0, fire_gfx_int x1) {
    FireGfxUnionSink* k = (FireGfxUnionSink*)self;
    fire_gfx_int i;
    if (x1 < x0) { fire_gfx_int t = x0; x0 = x1; x1 = t; }
    for (i = 0; i < k->pen->stampCount; i++) { const FireGfxStampRow* r = &k->pen->stamp[i]; fire_gfx_unionAdd(k, y + r->dy, x0 + r->dx0, x1 + r->dx1); }
}
static int fire_gfx_byRow(const void* pa, const void* pb) {
    const FireGfxRun* a = (const FireGfxRun*)pa;
    const FireGfxRun* b = (const FireGfxRun*)pb;
    if (a->y != b->y) return a->y < b->y ? -1 : 1;
    return (a->x0 > b->x0) - (a->x0 < b->x0);
}
static void fire_gfx_unionFlush(FireGfxUnionSink* k) {
    fire_gfx_size i = 0;
    if (k->count > 1) qsort(k->runs, k->count, sizeof(FireGfxRun), fire_gfx_byRow);
    while (i < k->count) {
        fire_gfx_int y = k->runs[i].y, x0 = k->runs[i].x0, x1 = k->runs[i].x1;
        i++;
        while (i < k->count && k->runs[i].y == y && k->runs[i].x0 <= x1 + 1) { x1 = fire_gfx_imax(x1, k->runs[i].x1); i++; }
        fire_gfx_span(k->surface, y, x0, x1, &k->pixel);
    }
}

/* What a stroke draws: one of the shapes with its numbers. */
enum { FIRE_GFX_STROKE_POINT, FIRE_GFX_STROKE_LINE, FIRE_GFX_STROKE_PATH, FIRE_GFX_STROKE_RECT, FIRE_GFX_STROKE_ELLIPSE, FIRE_GFX_STROKE_TRIANGLE };
typedef struct FireGfxStroke {
    fire_gfx_int kind;
    fire_gfx_int v[6];
    const fire_gfx_int* points;
    fire_gfx_size count;
    fire_gfx_int closed;
} FireGfxStroke;
static void fire_gfx_strokeEmit(FireGfxSink* k, const FireGfxStroke* st) {
    switch (st->kind) {
        case FIRE_GFX_STROKE_POINT: k->point(k, st->v[0], st->v[1]); break;
        case FIRE_GFX_STROKE_LINE: fire_gfx_shapeLine(k, st->v[0], st->v[1], st->v[2], st->v[3]); break;
        case FIRE_GFX_STROKE_PATH: fire_gfx_shapePolygon(k, st->points, st->count, st->closed); break;
        case FIRE_GFX_STROKE_RECT: fire_gfx_shapeRect(k, st->v[0], st->v[1], st->v[2], st->v[3]); break;
        case FIRE_GFX_STROKE_ELLIPSE: fire_gfx_shapeEllipse(k, st->v[0], st->v[1], st->v[2], st->v[3]); break;
        case FIRE_GFX_STROKE_TRIANGLE: fire_gfx_shapeTriangle(k, st->v[0], st->v[1], st->v[2], st->v[3], st->v[4], st->v[5]); break;
        default: break;
    }
}
static void fire_gfx_penStroke(const FireGfxPen* pen, FireGfxSurface* s, const FireGfxStroke* st) {
    FireGfxPixel pixel = fire_gfx_resolve(s, pen->color);
    if (!fire_gfx_visible(s, &pixel)) return;
    if (fire_gfx_isCopy(s, &pixel)) {
        FireGfxStampSink sink;
        sink.base.point = fire_gfx_stampPoint; sink.base.span = fire_gfx_stampSpan;
        sink.surface = s; sink.pixel = pixel; sink.pen = pen;
        fire_gfx_strokeEmit(&sink.base, st);
    } else {
        FireGfxUnionSink sink;
        sink.base.point = fire_gfx_unionPoint; sink.base.span = fire_gfx_unionSpan;
        sink.surface = s; sink.pixel = pixel; sink.pen = pen;
        sink.runs = NULL; sink.count = 0; sink.cap = 0;
        fire_gfx_strokeEmit(&sink.base, st);
        fire_gfx_unionFlush(&sink);
        fire_gfx_free(sink.runs);
    }
}
static FireGfxStroke fire_gfx_stroke(fire_gfx_int kind, fire_gfx_int a, fire_gfx_int b, fire_gfx_int c, fire_gfx_int d, fire_gfx_int e, fire_gfx_int f) {
    FireGfxStroke st;
    st.kind = kind; st.v[0] = a; st.v[1] = b; st.v[2] = c; st.v[3] = d; st.v[4] = e; st.v[5] = f;
    st.points = NULL; st.count = 0; st.closed = 0;
    return st;
}

static void fire_gfx_penDrawPoint(const FireGfxPen* p, FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y) { FireGfxStroke st = fire_gfx_stroke(FIRE_GFX_STROKE_POINT, x, y, 0, 0, 0, 0); fire_gfx_penStroke(p, s, &st); }
static void fire_gfx_penDrawLine(const FireGfxPen* p, FireGfxSurface* s, fire_gfx_int x0, fire_gfx_int y0, fire_gfx_int x1, fire_gfx_int y1) { FireGfxStroke st = fire_gfx_stroke(FIRE_GFX_STROKE_LINE, x0, y0, x1, y1, 0, 0); fire_gfx_penStroke(p, s, &st); }
static void fire_gfx_penDrawPath(const FireGfxPen* p, FireGfxSurface* s, const fire_gfx_int* points, fire_gfx_size count, fire_gfx_int closed) {
    FireGfxStroke st = fire_gfx_stroke(FIRE_GFX_STROKE_PATH, 0, 0, 0, 0, 0, 0);
    st.points = points; st.count = count; st.closed = closed;
    fire_gfx_penStroke(p, s, &st);
}
static void fire_gfx_penDrawRect(const FireGfxPen* p, FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h) { FireGfxStroke st = fire_gfx_stroke(FIRE_GFX_STROKE_RECT, x, y, w, h, 0, 0); fire_gfx_penStroke(p, s, &st); }
static void fire_gfx_penDrawCircle(const FireGfxPen* p, FireGfxSurface* s, fire_gfx_int cx, fire_gfx_int cy, fire_gfx_int r) { FireGfxStroke st = fire_gfx_stroke(FIRE_GFX_STROKE_ELLIPSE, cx, cy, r, r, 0, 0); fire_gfx_penStroke(p, s, &st); }
static void fire_gfx_penDrawEllipse(const FireGfxPen* p, FireGfxSurface* s, fire_gfx_int cx, fire_gfx_int cy, fire_gfx_int rx, fire_gfx_int ry) { FireGfxStroke st = fire_gfx_stroke(FIRE_GFX_STROKE_ELLIPSE, cx, cy, rx, ry, 0, 0); fire_gfx_penStroke(p, s, &st); }
static void fire_gfx_penDrawTriangle(const FireGfxPen* p, FireGfxSurface* s, fire_gfx_int x0, fire_gfx_int y0, fire_gfx_int x1, fire_gfx_int y1, fire_gfx_int x2, fire_gfx_int y2) { FireGfxStroke st = fire_gfx_stroke(FIRE_GFX_STROKE_TRIANGLE, x0, y0, x1, y1, x2, y2); fire_gfx_penStroke(p, s, &st); }
static void fire_gfx_penDrawPolygon(const FireGfxPen* p, FireGfxSurface* s, const fire_gfx_int* points, fire_gfx_size count, fire_gfx_int closed) { fire_gfx_penDrawPath(p, s, points, count, closed); }

/* ---- blitting --------------------------------------------------------------------------------------------------------------------------------- */
enum { FIRE_GFX_BLIT_COPY = 0, FIRE_GFX_BLIT_TRANSPARENT = 1, FIRE_GFX_BLIT_BLEND = 2 };

/* (a small cache for the palette lookup of RGBA sources) */
typedef struct FireGfxCachedIndex { uint32_t color; uint8_t index; uint8_t valid; } FireGfxCachedIndex;

/* Copies a region (scaled with nearest neighbor, flipped for a negative size) between framebuffers of any color mode. Without alpha blending the mode Blend acts like Transparent.
 * (The C++ had the clip rectangle as defaulted parameters: 0, 0, 0x7FFFFFFF, 0x7FFFFFFF for none.) */
static void fire_gfx_blit(FireGfxFramebuffer* dst, const FireGfxFramebuffer* srcIn, fire_gfx_int sx, fire_gfx_int sy, fire_gfx_int sw, fire_gfx_int sh, fire_gfx_int dx, fire_gfx_int dy, fire_gfx_int dw, fire_gfx_int dh, fire_gfx_int mode, fire_gfx_int colorKey, fire_gfx_int blend,
                          fire_gfx_int clipLeft, fire_gfx_int clipTop, fire_gfx_int clipRight, fire_gfx_int clipBottom) {
    int64_t absDw, absDh, dxStart, dxEnd, dyStart, dyEnd, i, j;
    fire_gfx_int flipX, flipY, directIndices, key, srcIndexed, dstIndexed;
    FireGfxFramebuffer snapshot;
    const FireGfxFramebuffer* src = srcIn;
    uint32_t srcTable[256];
    uint8_t map[256];
    FireGfxCachedIndex* cache = NULL;
    if (!blend && mode == FIRE_GFX_BLIT_BLEND) mode = FIRE_GFX_BLIT_TRANSPARENT;
    if (sw <= 0 || sh <= 0 || dw == 0 || dh == 0) return;
    fire_gfx_fbCpu(srcIn, 0);
    fire_gfx_fbCpu(dst, 1);
    absDw = llabs((int64_t)dw); absDh = llabs((int64_t)dh);
    flipX = dw < 0; flipY = dh < 0;

    /* the same buffer: read from a copy so that overlapping regions do not overwrite themselves */
    fire_gfx_fbInit(&snapshot);
    if (dst == srcIn) {
        fire_gfx_size n = fire_gfx_fbPixelCount(srcIn);
        snapshot.width = srcIn->width; snapshot.height = srcIn->height; snapshot.mode = srcIn->mode;
        snapshot.transparentIndex = srcIn->transparentIndex;
        if (srcIn->indices) { snapshot.indices = (uint8_t*)fire_gfx_malloc(n); if (!snapshot.indices) return; fire_gfx_copy(snapshot.indices, srcIn->indices, n); }
        else { snapshot.pixels = (uint32_t*)fire_gfx_malloc(n * 4); if (!snapshot.pixels) return; fire_gfx_copy(snapshot.pixels, srcIn->pixels, n * 4); }
        snapshot.palette = srcIn->palette;
        src = &snapshot;
    }
    srcIndexed = fire_gfx_fbIsIndexed(src);
    dstIndexed = fire_gfx_fbIsIndexed(dst);

    if (srcIndexed) fire_gfx_copy(srcTable, src->palette.entries, sizeof srcTable);
    directIndices = srcIndexed && dstIndexed && fire_gfx_paletteSame(&src->palette, &dst->palette);
    if (srcIndexed && dstIndexed && !directIndices) {
        fire_gfx_int k;
        for (k = 0; k < 256; k++) map[k] = fire_gfx_paletteNearest(&dst->palette, srcTable[k]);
    }
    key = srcIndexed ? (colorKey >= 0 ? colorKey : src->transparentIndex) : -1;

    dxStart = fire_gfx_lmax(fire_gfx_lmax(0, clipLeft) - (int64_t)dx, 0); dxEnd = fire_gfx_lmin(absDw, fire_gfx_lmin(dst->width, clipRight) - (int64_t)dx);
    dyStart = fire_gfx_lmax(fire_gfx_lmax(0, clipTop) - (int64_t)dy, 0); dyEnd = fire_gfx_lmin(absDh, fire_gfx_lmin(dst->height, clipBottom) - (int64_t)dy);
    if (dxEnd <= dxStart || dyEnd <= dyStart) goto done;

    /* the common case - RGBA to RGBA, 1:1, not flipped: whole rows (copy) or a plain loop (transparent, blend), no division per pixel */
    if (!srcIndexed && !dstIndexed && !flipX && !flipY && absDw == sw && absDh == sh) {
        int64_t i0 = fire_gfx_lmax(dxStart, -(int64_t)sx), i1 = fire_gfx_lmin(dxEnd, (int64_t)src->width - sx);
        int64_t j0 = fire_gfx_lmax(dyStart, -(int64_t)sy), j1 = fire_gfx_lmin(dyEnd, (int64_t)src->height - sy);
        if (i1 <= i0 || j1 <= j0) goto done;
        for (j = j0; j < j1; j++) {
            const uint32_t* from = src->pixels + (fire_gfx_size)(sy + j) * src->width + (fire_gfx_size)(sx + i0);
            uint32_t* to = dst->pixels + (fire_gfx_size)(dy + j) * dst->width + (fire_gfx_size)(dx + i0);
            fire_gfx_size n = (fire_gfx_size)(i1 - i0), k;
            if (mode == FIRE_GFX_BLIT_COPY) { fire_gfx_move(to, from, n * 4); continue; }
            for (k = 0; k < n; k++) {
                uint32_t c = from[k];
                if ((c >> 24) == 0) continue;
                to[k] = mode == FIRE_GFX_BLIT_BLEND ? fire_gfx_mix(to[k], c) : c;
            }
        }
        goto done;
    }

    /* a small cache for the palette lookup of RGBA sources (many equal colors, and a search costs 256 comparisons) */
    if (!srcIndexed && dstIndexed) {
        cache = (FireGfxCachedIndex*)fire_gfx_calloc(1024, sizeof(FireGfxCachedIndex));
        if (cache == NULL) goto done;
    }

    for (j = dyStart; j < dyEnd; j++) {
        int64_t fy = flipY ? absDh - 1 - j : j;
        int64_t srcY = sy + (2 * fy + 1) * sh / (2 * absDh);
        fire_gfx_int dstY;
        if (srcY < 0 || srcY >= src->height) continue;
        dstY = (fire_gfx_int)(dy + j);
        for (i = dxStart; i < dxEnd; i++) {
            int64_t fx = flipX ? absDw - 1 - i : i;
            int64_t srcX = sx + (2 * fx + 1) * sw / (2 * absDw);
            fire_gfx_int dstX;
            fire_gfx_size srcPos, dstPos;
            if (srcX < 0 || srcX >= src->width) continue;
            dstX = (fire_gfx_int)(dx + i);
            srcPos = (fire_gfx_size)(srcY * src->width + srcX);
            dstPos = (fire_gfx_size)dstY * dst->width + dstX;
            if (srcIndexed) {
                uint8_t idx = src->indices[srcPos];
                if (mode != FIRE_GFX_BLIT_COPY && idx == key) continue;
                if (dstIndexed) dst->indices[dstPos] = directIndices ? idx : map[idx];
                else {
                    uint32_t c = srcTable[idx];
                    if (mode == FIRE_GFX_BLIT_BLEND) c = fire_gfx_mix(dst->pixels[dstPos], c);
                    dst->pixels[dstPos] = c;
                }
            } else {
                uint32_t c = src->pixels[srcPos];
                uint32_t alpha = c >> 24;
                if (mode != FIRE_GFX_BLIT_COPY && alpha == 0) continue;
                if (dstIndexed) {
                    FireGfxCachedIndex* slot;
                    if (mode == FIRE_GFX_BLIT_BLEND && alpha < 128) continue;
                    slot = &cache[(c * 2654435761u) >> 22];
                    if (!slot->valid || slot->color != c) { slot->color = c; slot->index = fire_gfx_paletteNearest(&dst->palette, c); slot->valid = 1; }
                    dst->indices[dstPos] = slot->index;
                } else dst->pixels[dstPos] = mode == FIRE_GFX_BLIT_BLEND ? fire_gfx_mix(dst->pixels[dstPos], c) : c;
            }
        }
    }
done:
    fire_gfx_free(cache);
    fire_gfx_fbFreePixels(&snapshot);
}

/* ---- the renderer: terminal text and the drawing functions of a framebuffer ---------------------------------------------------------------------------------- */
typedef struct FireGfxRenderer {
    FireGfxFramebuffer* target;
    fire_gfx_int smallFont;      /* the built-in font is 8x14 (the default) or 8x8 */
    fire_gfx_int alphaBlending;
    fire_gfx_int cellWidth, cellHeight, cursorRow, cursorColumn;
    FireGfxPaint foreground;
    FireGfxPaint background;
    fire_gfx_int hasBackground;
    fire_gfx_int clipLeft, clipTop, clipRight, clipBottom;
} FireGfxRenderer;

/* A new renderer on `fb` (it holds a reference of the framebuffer), NULL without memory. */
static FireGfxRenderer* fire_gfx_rendererNew(FireGfxFramebuffer* fb, fire_gfx_int small) {
    FireGfxRenderer* r = (FireGfxRenderer*)fire_gfx_malloc(sizeof(FireGfxRenderer));
    if (r == NULL) return NULL;
    r->target = fb;
    r->smallFont = small;
    r->alphaBlending = 1;
    r->cellWidth = 8; r->cellHeight = small ? 8 : 14; r->cursorRow = 0; r->cursorColumn = 0;
    r->foreground = fire_gfx_paintRgba(fire_gfx_rgb(255, 255, 255));
    r->background = fire_gfx_paintRgba(fire_gfx_rgb(0, 0, 0));
    r->hasBackground = 1;
    r->clipLeft = 0; r->clipTop = 0; r->clipRight = 0x7FFFFFFF; r->clipBottom = 0x7FFFFFFF;
    fire_gfx_fbRetain(fb);
    return r;
}
static void fire_gfx_rendererDelete(FireGfxRenderer* r) { if (r == NULL) return; fire_gfx_fbRelease(r->target); fire_gfx_free(r); }

static FireGfxSurface fire_gfx_rendererSurface(const FireGfxRenderer* r) { return fire_gfx_surface(r->target, r->alphaBlending, r->clipLeft, r->clipTop, r->clipRight, r->clipBottom); }
static void fire_gfx_rendererSetClip(FireGfxRenderer* r, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h) {
    r->clipLeft = x; r->clipTop = y;
    r->clipRight = (fire_gfx_int)fire_gfx_lmin((int64_t)x + fire_gfx_imax(0, w), 0x7FFFFFFF);
    r->clipBottom = (fire_gfx_int)fire_gfx_lmin((int64_t)y + fire_gfx_imax(0, h), 0x7FFFFFFF);
}
static void fire_gfx_rendererResetClip(FireGfxRenderer* r) { r->clipLeft = 0; r->clipTop = 0; r->clipRight = 0x7FFFFFFF; r->clipBottom = 0x7FFFFFFF; }
/* the grid follows the size of the target (a framebuffer may change its size, see fire_gfx_fbResize); the cursor is kept inside it */
static fire_gfx_int fire_gfx_rendererColumns(const FireGfxRenderer* r) { return r->target->width / r->cellWidth; }
static fire_gfx_int fire_gfx_rendererRows(const FireGfxRenderer* r) { return r->target->height / r->cellHeight; }
static void fire_gfx_rendererClampCursor(FireGfxRenderer* r) {
    r->cursorRow = fire_gfx_imax(0, fire_gfx_imin(r->cursorRow, fire_gfx_imax(0, fire_gfx_rendererRows(r) - 1)));
    r->cursorColumn = fire_gfx_imax(0, fire_gfx_imin(r->cursorColumn, fire_gfx_imax(0, fire_gfx_rendererColumns(r) - 1)));
}
static const uint8_t* fire_gfx_rendererGlyph(const FireGfxRenderer* r, uint32_t c) { uint32_t i = c < 256 ? c : '?'; return r->smallFont ? g_fireGfxGlyphs8x8[i] : g_fireGfxGlyphs8x14[i]; }

static void fire_gfx_rendererLocate(FireGfxRenderer* r, fire_gfx_int row, fire_gfx_int column) {
    r->cursorRow = fire_gfx_imax(0, fire_gfx_imin(row, fire_gfx_imax(0, fire_gfx_rendererRows(r) - 1)));
    r->cursorColumn = fire_gfx_imax(0, fire_gfx_imin(column, fire_gfx_imax(0, fire_gfx_rendererColumns(r) - 1)));
}

/* Clear and scrolling set pixels as they are (no blending) */
static FireGfxPixel fire_gfx_rendererClearPixel(const FireGfxRenderer* r) {
    FireGfxSurface s = fire_gfx_rendererSurface(r);
    return fire_gfx_resolve(&s, r->hasBackground ? r->background : fire_gfx_paintRgba(fire_gfx_rgb(0, 0, 0)));
}
static void fire_gfx_rendererClearAll(FireGfxRenderer* r, const FireGfxPixel* p) {
    fire_gfx_size n = fire_gfx_fbPixelCount(r->target), k;
    fire_gfx_fbCpu(r->target, 1);
    if (r->target->indices) fire_gfx_set(r->target->indices, p->index, n);
    else for (k = 0; k < n; k++) r->target->pixels[k] = p->rgba;
}
static void fire_gfx_rendererClear(FireGfxRenderer* r) { FireGfxPixel p = fire_gfx_rendererClearPixel(r); fire_gfx_rendererClearAll(r, &p); r->cursorRow = 0; r->cursorColumn = 0; }
static void fire_gfx_rendererClearTo(FireGfxRenderer* r, FireGfxPaint paint) { FireGfxSurface s = fire_gfx_rendererSurface(r); FireGfxPixel p = fire_gfx_resolve(&s, paint); fire_gfx_rendererClearAll(r, &p); }
static void fire_gfx_rendererSetColor(FireGfxRenderer* r, FireGfxPaint fg, FireGfxPaint bg) { r->foreground = fg; r->background = bg; r->hasBackground = 1; }

static void fire_gfx_rendererScrollUp(FireGfxRenderer* r, fire_gfx_int pixelRows, const FireGfxPixel* fill) {
    fire_gfx_int w = r->target->width, h = r->target->height;
    fire_gfx_size keep, moved, k;
    if (pixelRows <= 0) return;
    fire_gfx_fbCpu(r->target, 1);
    if (pixelRows >= h) { fire_gfx_rendererClearAll(r, fill); return; }
    keep = (fire_gfx_size)(h - pixelRows) * w; moved = (fire_gfx_size)pixelRows * w;
    if (r->target->indices) { fire_gfx_move(r->target->indices, r->target->indices + moved, keep); fire_gfx_set(r->target->indices + keep, fill->index, moved); return; }
    fire_gfx_move(r->target->pixels, r->target->pixels + moved, keep * 4);
    for (k = 0; k < moved; k++) r->target->pixels[keep + k] = fill->rgba;
}

/* One character with its upper left corner at (x, y): a background that is not visible is none, a foreground that is not visible draws no glyph pixels. */
static void fire_gfx_rendererDrawGlyph(FireGfxRenderer* r, FireGfxSurface* s, fire_gfx_int x, fire_gfx_int y, uint32_t c, const FireGfxPixel* fg, fire_gfx_int hasBg, const FireGfxPixel* bg) {
    fire_gfx_int fgVisible, gx, gy;
    const uint8_t* rowsBits;
    if (hasBg && !fire_gfx_visible(s, bg)) hasBg = 0;
    fgVisible = fire_gfx_visible(s, fg);
    if (!fgVisible && !hasBg) return;
    if (hasBg) fire_gfx_rect(s, x, y, r->cellWidth, r->cellHeight, bg);
    if (!fgVisible) return;
    rowsBits = fire_gfx_rendererGlyph(r, c);
    for (gy = 0; gy < r->cellHeight; gy++) {
        uint32_t bits = rowsBits[gy];
        if (bits == 0) continue;
        for (gx = 0; gx < r->cellWidth; gx++)
            if ((bits >> (7 - gx)) & 1u) fire_gfx_put(s, x + gx, y + gy, fg);
    }
}

static void fire_gfx_rendererNewLine(FireGfxRenderer* r) {
    r->cursorColumn = 0;
    r->cursorRow++;
    if (r->cursorRow >= fire_gfx_rendererRows(r)) { FireGfxPixel p = fire_gfx_rendererClearPixel(r); fire_gfx_rendererScrollUp(r, r->cellHeight, &p); r->cursorRow = fire_gfx_rendererRows(r) - 1; }
}
static void fire_gfx_rendererAdvance(FireGfxRenderer* r) { r->cursorColumn++; if (r->cursorColumn >= fire_gfx_rendererColumns(r)) fire_gfx_rendererNewLine(r); }

/* `text`: UTF-16 code units (fire_char16 of the runtime, or any 16-bit unit). */
static void fire_gfx_rendererPrint(FireGfxRenderer* r, const uint16_t* text, uint32_t n) {
    FireGfxSurface s;
    FireGfxPixel fg, bg;
    uint32_t i;
    fire_gfx_rendererClampCursor(r);
    s = fire_gfx_rendererSurface(r);
    fg = fire_gfx_resolve(&s, r->foreground);
    bg = r->hasBackground ? fire_gfx_resolve(&s, r->background) : fire_gfx_pixelNone();
    for (i = 0; i < n; i++) {
        uint16_t c = text[i];
        if (c == '\n') { fire_gfx_rendererNewLine(r); continue; }
        if (c == '\r') continue;
        fire_gfx_rendererDrawGlyph(r, &s, r->cursorColumn * r->cellWidth, r->cursorRow * r->cellHeight, c, &fg, r->hasBackground, &bg);
        fire_gfx_rendererAdvance(r);
    }
}

/* Text at pixel coordinates: the glyph pixels in the color of `fg`, with `bg` (NULL = none) the whole cell below. */
static void fire_gfx_rendererDrawText(FireGfxRenderer* r, fire_gfx_int x, fire_gfx_int y, const uint16_t* text, uint32_t n, const FireGfxBrush* fg, const FireGfxBrush* bg) {
    FireGfxSurface s = fire_gfx_rendererSurface(r);
    FireGfxPixel f = fire_gfx_resolve(&s, fg->color);
    FireGfxPixel b = bg ? fire_gfx_resolve(&s, bg->color) : fire_gfx_pixelNone();
    uint32_t i;
    for (i = 0; i < n; i++) { fire_gfx_rendererDrawGlyph(r, &s, x, y, text[i], &f, bg != NULL, &b); x += r->cellWidth; }
}

#endif
