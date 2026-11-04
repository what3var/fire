/* fire graphics package: drawing helpers and effects in software (docs/spec/UI_PLATFORM.md, "Zeichnen: Helfer und Effekte") - for the VM and native builds alike, and the
 * reference for a GPU backend later.
 *
 *   fire_gfx_fxFillRoundRect    a rounded rectangle, antialiased at its edges (coverage per pixel only where the edge is; the inside is spans)
 *   fire_gfx_fxStrokeRoundRect  its outline, `width` pixels wide, antialiased
 *   fire_gfx_fxShadow           the fine shadow of a rounded rectangle: its mask at 1/2 or 1/4 of the size, blurred, kept in a small cache, stretched
 *                               bilinearly; only the ring outside the rectangle is drawn (what is under it is covered anyway)
 *   fire_gfx_fxBlur             blurs what is in a rounded rectangle: smaller by 2 or 4, two box blurs with a running sum (the cost does not grow with the radius),
 *                               bilinearly back
 *   fire_gfx_fxMaterial         glass and acrylic: the blur, a little more saturation, a tint, (acrylic) a fixed fine grain, a hairline and a light rim of 1-2 px;
 *                               with switches for blur and transparency (without them it becomes a tint, or an opaque color)
 *
 * They draw into RGBA framebuffers; a palette framebuffer gets the plain shape (a pixel counts from half coverage on, effects only tint). Positions and sizes are
 * pixels; a radius is clamped to half the shorter side. */
#ifndef FIRE_GFX_FX_H
#define FIRE_GFX_FX_H

#include <math.h>
#include "fire_gfx.h"

/* ---- blending ---------------------------------------------------------------------------------------------------------------------------------------------- */

/* `c` with its alpha scaled by the coverage `cov` (0 to 255). */
static uint32_t fire_gfx_fxCover(uint32_t c, unsigned cov) {
    uint32_t a = ((c >> 24) * cov + 127u) / 255u;
    return (c & 0x00FFFFFFu) | (a << 24);
}

/* One pixel of color `c` with coverage `cov`, clipped. */
static void fire_gfx_fxPut(FireGfxSurface* s, int x, int y, const FireGfxPixel* p, unsigned cov) {
    size_t i;
    if (cov == 0 || x < s->clipLeft || x >= s->clipRight || y < s->clipTop || y >= s->clipBottom) return;
    i = (size_t)y * s->width + x;
    if (s->indices) { if (cov >= 128 && fire_gfx_visible(s, p)) s->indices[i] = p->index; return; }
    if (!s->blend) { if (cov >= 128) s->pixels[i] = p->rgba; return; }
    if (cov >= 255) { if ((p->rgba >> 24) != 0) s->pixels[i] = (p->rgba >> 24) == 255 ? p->rgba : fire_gfx_mix(s->pixels[i], p->rgba); return; }
    {
        uint32_t c = fire_gfx_fxCover(p->rgba, cov);
        if ((c >> 24) != 0) s->pixels[i] = fire_gfx_mix(s->pixels[i], c);
    }
}

/* A span (x0..x1 included) with coverage `cov`, clipped. */
static void fire_gfx_fxSpan(FireGfxSurface* s, int y, int x0, int x1, const FireGfxPixel* p, unsigned cov) {
    if (cov == 0) return;
    if (cov >= 255 || s->indices || !s->blend) { if (cov >= 128) fire_gfx_span(s, y, x0, x1, p); return; }
    if (y < s->clipTop || y >= s->clipBottom) return;
    x0 = fire_gfx_imax(s->clipLeft, x0); x1 = fire_gfx_imin(s->clipRight - 1, x1);
    if (x1 < x0) return;
    fire_gfx_blendRow(s->pixels + (size_t)y * s->width + x0, (size_t)(x1 - x0 + 1), fire_gfx_fxCover(p->rgba, cov));
}

/* ---- the geometry of a rounded rectangle ------------------------------------------------------------------------------------------------------------------- */

typedef struct FireGfxRoundRect { double cx, cy, hx, hy, r; } FireGfxRoundRect;

static FireGfxRoundRect fire_gfx_fxRoundRect(double x, double y, double w, double h, double r) {
    FireGfxRoundRect q;
    q.hx = w / 2; q.hy = h / 2;
    q.cx = x + q.hx; q.cy = y + q.hy;
    if (r < 0) r = 0;
    if (r > q.hx) r = q.hx;
    if (r > q.hy) r = q.hy;
    q.r = r;
    return q;
}

/* The signed distance of the point (px, py) to the edge (< 0 inside). */
static double fire_gfx_fxDistance(const FireGfxRoundRect* q, double px, double py) {
    double qx = fabs(px - q->cx) - (q->hx - q->r), qy = fabs(py - q->cy) - (q->hy - q->r);
    double ox = qx > 0 ? qx : 0, oy = qy > 0 ? qy : 0;
    double inside = qx > qy ? qx : qy;
    return sqrt(ox * ox + oy * oy) + (inside < 0 ? inside : 0) - q->r;
}

/* How much of the pixel (x, y) is inside (0 to 255). */
static unsigned fire_gfx_fxCoverage(const FireGfxRoundRect* q, int x, int y) {
    double c = 0.5 - fire_gfx_fxDistance(q, x + 0.5, y + 0.5);
    return c <= 0 ? 0u : c >= 1 ? 255u : (unsigned)(c * 255.0 + 0.5);
}

/* ---- rounded rectangles ------------------------------------------------------------------------------------------------------------------------------------ */

static void fire_gfx_fxFillRoundRect(FireGfxSurface* s, int x, int y, int w, int h, int radius, const FireGfxPixel* p) {
    FireGfxRoundRect q;
    int yy, y0, y1, band;
    if (w <= 0 || h <= 0 || !fire_gfx_visible(s, p)) return;
    q = fire_gfx_fxRoundRect(x, y, w, h, radius);
    band = (int)ceil(q.r);
    if (band == 0) { fire_gfx_rect(s, x, y, w, h, p); return; }
    y0 = fire_gfx_imax(s->clipTop, y); y1 = fire_gfx_imin(s->clipBottom, y + h);
    for (yy = y0; yy < y1; yy++) {
        int xx;
        if (yy >= y + band && yy < y + h - band) { fire_gfx_span(s, yy, x, x + w - 1, p); continue; }
        /* a row of the corners: the corners pixel by pixel, the part between them at once */
        for (xx = x; xx < x + band && xx < x + w; xx++) fire_gfx_fxPut(s, xx, yy, p, fire_gfx_fxCoverage(&q, xx, yy));
        if (x + band <= x + w - band - 1) fire_gfx_fxSpan(s, yy, x + band, x + w - band - 1, p, fire_gfx_fxCoverage(&q, x + w / 2, yy));
        for (xx = fire_gfx_imax(x + band, x + w - band); xx < x + w; xx++) fire_gfx_fxPut(s, xx, yy, p, fire_gfx_fxCoverage(&q, xx, yy));
    }
}

static void fire_gfx_fxStrokeRoundRect(FireGfxSurface* s, int x, int y, int w, int h, int radius, int width, const FireGfxPixel* p) {
    FireGfxRoundRect outer, inner;
    int yy, y0, y1, band;
    if (w <= 0 || h <= 0 || width <= 0 || !fire_gfx_visible(s, p)) return;
    if (2 * width >= w || 2 * width >= h) { fire_gfx_fxFillRoundRect(s, x, y, w, h, radius, p); return; }
    outer = fire_gfx_fxRoundRect(x, y, w, h, radius);
    inner = fire_gfx_fxRoundRect(x + width, y + width, w - 2 * width, h - 2 * width, outer.r - width);
    band = fire_gfx_imax((int)ceil(outer.r), width + (int)ceil(inner.r)) + 1;
    y0 = fire_gfx_imax(s->clipTop, y); y1 = fire_gfx_imin(s->clipBottom, y + h);
    for (yy = y0; yy < y1; yy++) {
        int xx;
        if (yy >= y + band && yy < y + h - band) {
            /* between the corners: the two sides */
            fire_gfx_span(s, yy, x, x + width - 1, p);
            fire_gfx_span(s, yy, x + w - width, x + w - 1, p);
            continue;
        }
        if (2 * band >= w) {
            for (xx = x; xx < x + w; xx++) {
                int c = (int)fire_gfx_fxCoverage(&outer, xx, yy) - (int)fire_gfx_fxCoverage(&inner, xx, yy);
                if (c > 0) fire_gfx_fxPut(s, xx, yy, p, (unsigned)c);
            }
            continue;
        }
        for (xx = x; xx < x + band; xx++) {
            int c = (int)fire_gfx_fxCoverage(&outer, xx, yy) - (int)fire_gfx_fxCoverage(&inner, xx, yy);
            if (c > 0) fire_gfx_fxPut(s, xx, yy, p, (unsigned)c);
        }
        {
            int c = (int)fire_gfx_fxCoverage(&outer, x + w / 2, yy) - (int)fire_gfx_fxCoverage(&inner, x + w / 2, yy);
            if (c > 0) fire_gfx_fxSpan(s, yy, x + band, x + w - band - 1, p, (unsigned)c);
        }
        for (xx = x + w - band; xx < x + w; xx++) {
            int c = (int)fire_gfx_fxCoverage(&outer, xx, yy) - (int)fire_gfx_fxCoverage(&inner, xx, yy);
            if (c > 0) fire_gfx_fxPut(s, xx, yy, p, (unsigned)c);
        }
    }
}

/* ---- box blur with a running sum --------------------------------------------------------------------------------------------------------------------------- */

/* Blurs `n` values of one channel (step `stride` apart) with a box of radius r, twice (close to a Gaussian); the edges repeat. `tmp` holds n values. */
static void fire_gfx_fxBox(uint8_t* v, int n, int stride, int r, uint8_t* tmp) {
    int pass, i;
    if (r <= 0 || n <= 1) return;
    for (pass = 0; pass < 2; pass++) {
        int32_t sum = 0, div = 2 * r + 1;
        for (i = -r; i <= r; i++) sum += v[(size_t)fire_gfx_imax(0, fire_gfx_imin(n - 1, i)) * stride];
        for (i = 0; i < n; i++) {
            tmp[i] = (uint8_t)((sum + div / 2) / div);
            sum += v[(size_t)fire_gfx_imin(n - 1, i + r + 1) * stride] - v[(size_t)fire_gfx_imax(0, i - r) * stride];
        }
        for (i = 0; i < n; i++) v[(size_t)i * stride] = tmp[i];
    }
}

/* Blurs a picture of `w` x `h` with `channels` bytes per pixel, each channel on its own. */
static int fire_gfx_fxBlurPlanes(uint8_t* img, int w, int h, int channels, int r) {
    int y, x, c;
    uint8_t* tmp;
    if (r <= 0) return 1;
    tmp = (uint8_t*)malloc((size_t)fire_gfx_imax(w, h));
    if (tmp == NULL) return 0;
    for (y = 0; y < h; y++) for (c = 0; c < channels; c++) fire_gfx_fxBox(img + (size_t)y * w * channels + c, w, channels, r, tmp);
    for (x = 0; x < w; x++) for (c = 0; c < channels; c++) fire_gfx_fxBox(img + (size_t)x * channels + c, h, w * channels, r, tmp);
    free(tmp);
    return 1;
}

/* The value at (fx, fy) of a picture with `channels` bytes per pixel, bilinear (16.16 fixed point; outside: the edge). */
static void fire_gfx_fxSample(const uint8_t* img, int w, int h, int channels, int32_t fx, int32_t fy, int* out) {
    int x0, y0, x1, y1, c;
    uint32_t ax, ay;
    if (fx < 0) fx = 0;
    if (fy < 0) fy = 0;
    x0 = fx >> 16; y0 = fy >> 16;
    if (x0 >= w - 1) { x0 = w - 1; fx = x0 << 16; }
    if (y0 >= h - 1) { y0 = h - 1; fy = y0 << 16; }
    x1 = fire_gfx_imin(w - 1, x0 + 1); y1 = fire_gfx_imin(h - 1, y0 + 1);
    ax = ((uint32_t)fx >> 8) & 0xFF; ay = ((uint32_t)fy >> 8) & 0xFF;
    for (c = 0; c < channels; c++) {
        uint32_t a = img[((size_t)y0 * w + x0) * channels + c], b = img[((size_t)y0 * w + x1) * channels + c];
        uint32_t d = img[((size_t)y1 * w + x0) * channels + c], e = img[((size_t)y1 * w + x1) * channels + c];
        uint32_t top = a * (256 - ax) + b * ax, bottom = d * (256 - ax) + e * ax;
        out[c] = (int)((top * (256 - ay) + bottom * ay + 32768) >> 16);
    }
}

/* ---- shadows ----------------------------------------------------------------------------------------------------------------------------------------------- */

#define FIRE_GFX_SHADOW_CACHE 16
typedef struct FireGfxShadowMask { int w, h, r, soft; int mw, mh, scale, pad; uint8_t* mask; unsigned used; } FireGfxShadowMask;
static FireGfxShadowMask g_fireGfxShadows[FIRE_GFX_SHADOW_CACHE];
static unsigned g_fireGfxShadowClock;

/* The blurred mask of a rounded rectangle w x h (radius r, softness `soft`) with `pad` around it, at 1/scale of the size - from the cache, made when missing. */
static FireGfxShadowMask* fire_gfx_fxShadowMask(int w, int h, int r, int soft) {
    int i, oldest = 0, x, y;
    FireGfxShadowMask* m;
    FireGfxRoundRect q;
    for (i = 0; i < FIRE_GFX_SHADOW_CACHE; i++) {
        m = &g_fireGfxShadows[i];
        if (m->mask != NULL && m->w == w && m->h == h && m->r == r && m->soft == soft) { m->used = ++g_fireGfxShadowClock; return m; }
        if (m->used < g_fireGfxShadows[oldest].used) oldest = i;
    }
    m = &g_fireGfxShadows[oldest];
    free(m->mask);
    memset(m, 0, sizeof *m);
    m->w = w; m->h = h; m->r = r; m->soft = soft;
    m->scale = soft >= 8 ? 4 : soft >= 3 ? 2 : 1;
    m->pad = soft + m->scale;
    m->mw = (w + 2 * m->pad + m->scale - 1) / m->scale;
    m->mh = (h + 2 * m->pad + m->scale - 1) / m->scale;
    if ((int64_t)m->mw * m->mh > 4 * 1024 * 1024) { m->w = 0; return NULL; }
    m->mask = (uint8_t*)malloc((size_t)m->mw * m->mh);
    if (m->mask == NULL) { m->w = 0; return NULL; }
    /* the rectangle at the small size, antialiased - then blurred by the softness (two box passes of about half of it) */
    q = fire_gfx_fxRoundRect((double)m->pad / m->scale, (double)m->pad / m->scale, (double)w / m->scale, (double)h / m->scale, (double)r / m->scale);
    for (y = 0; y < m->mh; y++) for (x = 0; x < m->mw; x++) m->mask[(size_t)y * m->mw + x] = (uint8_t)fire_gfx_fxCoverage(&q, x, y);
    fire_gfx_fxBlurPlanes(m->mask, m->mw, m->mh, 1, (soft + m->scale) / (2 * m->scale));
    m->used = ++g_fireGfxShadowClock;
    return m;
}

/* The shadow of the rounded rectangle (x, y, w, h, radius): `color`'s alpha is its strongest opacity, `soft` how far it fades out, `dy` how far it is moved down.
 * The rectangle itself is left out (it is drawn over the shadow). */
static void fire_gfx_fxShadow(FireGfxSurface* s, int x, int y, int w, int h, int radius, int soft, uint32_t color, int dy) {
    FireGfxShadowMask* m;
    FireGfxRoundRect hole;
    int yy, xx, ox, oy, x0, x1, y0, y1;
    uint32_t a = color >> 24;
    if (w <= 0 || h <= 0 || a == 0 || soft <= 0) return;
    if (s->indices || !s->blend) return;   /* (a palette or a copy: no shadow) */
    if (soft > 256) soft = 256;
    m = fire_gfx_fxShadowMask(w, h, radius, soft);
    if (m == NULL) return;
    hole = fire_gfx_fxRoundRect(x, y, w, h, radius);
    ox = x - m->pad; oy = y - m->pad + dy;
    x0 = fire_gfx_imax(s->clipLeft, ox); x1 = fire_gfx_imin(s->clipRight, ox + m->mw * m->scale);
    y0 = fire_gfx_imax(s->clipTop, oy); y1 = fire_gfx_imin(s->clipBottom, oy + m->mh * m->scale);
    for (yy = y0; yy < y1; yy++) {
        uint32_t* row = s->pixels + (size_t)yy * s->width;
        int32_t fy = (int32_t)((((int64_t)(yy - oy) * 2 + 1) * 65536) / (2 * m->scale) - 32768);
        int band = (int)ceil(hole.r);
        int inRect = yy >= y && yy < y + h, insideRow = yy >= y + band && yy < y + h - band;
        for (xx = x0; xx < x1; xx++) {
            int v, c;
            unsigned cover = 0;
            int32_t fx;
            /* under the rectangle it is covered: the rows between the corners entirely, the others between their corners */
            if (inRect && xx >= x && xx < x + w) {
                if (insideRow) { xx = x + w - 1; continue; }
                if (xx >= x + band && xx < x + w - band) { xx = x + w - band - 1; continue; }
                cover = fire_gfx_fxCoverage(&hole, xx, yy);
                if (cover >= 255) continue;
            }
            fx = (int32_t)((((int64_t)(xx - ox) * 2 + 1) * 65536) / (2 * m->scale) - 32768);
            fire_gfx_fxSample(m->mask, m->mw, m->mh, 1, fx, fy, &v);
            c = (int)((a * (uint32_t)v * (255u - cover) + 32512u) / 65025u);
            if (c > 0) {
                uint32_t src = (color & 0x00FFFFFFu) | ((uint32_t)c << 24);
                row[xx] = fire_gfx_mix(row[xx], src);
            }
        }
    }
}

/* ---- blur and materials ------------------------------------------------------------------------------------------------------------------------------------ */

enum { FIRE_GFX_MATERIAL_TINT = 0, FIRE_GFX_MATERIAL_GLASS = 1, FIRE_GFX_MATERIAL_ACRYLIC = 2 };
enum { FIRE_GFX_FX_BLUR = 1, FIRE_GFX_FX_TRANSPARENCY = 2, FIRE_GFX_FX_DARK = 4 };

/* The fine grain of acrylic: a fixed value per pixel of the screen (-3 to 3), not per picture - it does not move. */
static int fire_gfx_fxGrain(int x, int y) {
    uint32_t h = (uint32_t)(x & 63) * 0x9E3779B1u ^ (uint32_t)(y & 63) * 0x85EBCA77u;
    h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
    return (int)(h % 7u) - 3;
}

static int fire_gfx_fxClamp255(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

/* What a material does with the blurred background (r, g, b): more saturation, the tint over it, the grain and - in the rim - a little light. */
static uint32_t fire_gfx_fxShade(int r, int g, int b, int saturation256, uint32_t tint, int grain, int light) {
    int l = (r * 77 + g * 150 + b * 29) >> 8;
    uint32_t ta = tint >> 24, inv = 255 - ta;
    r = l + (((r - l) * saturation256) >> 8);
    g = l + (((g - l) * saturation256) >> 8);
    b = l + (((b - l) * saturation256) >> 8);
    r = (int)(((uint32_t)fire_gfx_fxClamp255(r) * inv + (tint & 0xFF) * ta + 127) / 255) + grain;
    g = (int)(((uint32_t)fire_gfx_fxClamp255(g) * inv + ((tint >> 8) & 0xFF) * ta + 127) / 255) + grain;
    b = (int)(((uint32_t)fire_gfx_fxClamp255(b) * inv + ((tint >> 16) & 0xFF) * ta + 127) / 255) + grain;
    if (light > 0) { r += ((255 - r) * light) >> 8; g += ((255 - g) * light) >> 8; b += ((255 - b) * light) >> 8; }
    return (uint32_t)fire_gfx_fxClamp255(r) | ((uint32_t)fire_gfx_fxClamp255(g) << 8) | ((uint32_t)fire_gfx_fxClamp255(b) << 16) | 0xFF000000u;
}

/* Blurs the rounded rectangle (radius `corner`) by `radius` and - with `kind` GLASS/ACRYLIC - makes the material of it (saturation, tint, grain, rim). 0 without memory. */
static int fire_gfx_fxBlurMaterial(FireGfxSurface* s, int x, int y, int w, int h, int corner, int radius, int kind, uint32_t tint, int rimLight) {
    FireGfxRoundRect q;
    int f, pad, bx0, by0, bx1, by1, tw, th, yy, xx, sat, band;
    uint8_t* img;
    if (w <= 0 || h <= 0 || s->indices) return 1;
    q = fire_gfx_fxRoundRect(x, y, w, h, corner);
    band = (int)ceil(q.r);
    f = radius >= 10 ? 4 : radius >= 2 ? 2 : 1;
    /* what lies around the rectangle counts as well (no dark seam at its edge): `pad` more on each side, within the framebuffer */
    pad = radius + f;
    bx0 = fire_gfx_imax(0, x - pad); by0 = fire_gfx_imax(0, y - pad);
    bx1 = fire_gfx_imin(s->width, x + w + pad); by1 = fire_gfx_imin(s->height, y + h + pad);
    if (bx1 <= bx0 || by1 <= by0) return 1;
    tw = (bx1 - bx0 + f - 1) / f; th = (by1 - by0 + f - 1) / f;
    img = (uint8_t*)malloc((size_t)tw * th * 3);
    if (img == NULL) return 0;
    /* smaller: the average of f x f pixels */
    for (yy = 0; yy < th; yy++) for (xx = 0; xx < tw; xx++) {
        uint32_t sr = 0, sg = 0, sb = 0, n = 0;
        int sy, sx;
        for (sy = by0 + yy * f; sy < fire_gfx_imin(by1, by0 + yy * f + f); sy++)
            for (sx = bx0 + xx * f; sx < fire_gfx_imin(bx1, bx0 + xx * f + f); sx++) {
                uint32_t c = s->pixels[(size_t)sy * s->width + sx];
                sr += c & 0xFF; sg += (c >> 8) & 0xFF; sb += (c >> 16) & 0xFF; n++;
            }
        img[((size_t)yy * tw + xx) * 3] = (uint8_t)(sr / n);
        img[((size_t)yy * tw + xx) * 3 + 1] = (uint8_t)(sg / n);
        img[((size_t)yy * tw + xx) * 3 + 2] = (uint8_t)(sb / n);
    }
    if (!fire_gfx_fxBlurPlanes(img, tw, th, 3, (radius + f) / (2 * f))) { free(img); return 0; }
    sat = kind == FIRE_GFX_MATERIAL_GLASS ? 307 : kind == FIRE_GFX_MATERIAL_ACRYLIC ? 282 : 256;   /* (x 1.2, x 1.1, as it is) */
    if (kind == FIRE_GFX_MATERIAL_TINT) tint = 0;
    /* back to the full size, bilinear, into the rectangle (its edge mixed by the coverage) */
    for (yy = fire_gfx_imax(s->clipTop, y); yy < fire_gfx_imin(s->clipBottom, y + h); yy++) {
        uint32_t* row = s->pixels + (size_t)yy * s->width;
        int cornerRow = yy < y + band || yy >= y + h - band;
        for (xx = fire_gfx_imax(s->clipLeft, x); xx < fire_gfx_imin(s->clipRight, x + w); xx++) {
            int v[3], light = 0, sx = xx, sy = yy;
            unsigned cover = cornerRow && (xx < x + band || xx >= x + w - band) ? fire_gfx_fxCoverage(&q, xx, yy) : 255u;
            uint32_t out;
            if (cover == 0) continue;
            /* the rim: in the outer 2 pixels the background a pixel further in, a little lighter - like the hairline, not a lens */
            if (rimLight > 0) {
                double d = -fire_gfx_fxDistance(&q, xx + 0.5, yy + 0.5);
                if (d < 2.0) {
                    light = d < 1.0 ? rimLight : rimLight / 2;
                    sx += xx < q.cx ? 1 : -1;
                    sy += yy < q.cy ? 1 : -1;
                }
            }
            fire_gfx_fxSample(img, tw, th, 3, (int32_t)((((int64_t)(sx - bx0) * 2 + 1) * 65536) / (2 * f) - 32768),
                              (int32_t)((((int64_t)(sy - by0) * 2 + 1) * 65536) / (2 * f) - 32768), v);
            out = fire_gfx_fxShade(v[0], v[1], v[2], sat, tint, kind == FIRE_GFX_MATERIAL_ACRYLIC ? fire_gfx_fxGrain(xx, yy) : 0, light);
            row[xx] = cover >= 255 ? out : fire_gfx_mix(row[xx], fire_gfx_fxCover(out, cover));
        }
    }
    free(img);
    return 1;
}

/* Blurs what is in the rounded rectangle (x, y, w, h, corner) by `radius` pixels. */
static void fire_gfx_fxBlur(FireGfxSurface* s, int x, int y, int w, int h, int corner, int radius) {
    if (radius <= 0) return;
    if (radius > 64) radius = 64;
    fire_gfx_fxBlurMaterial(s, x, y, w, h, corner, radius, FIRE_GFX_MATERIAL_TINT, 0, 0);
}

/* The hairline of a material: 1 px inside the edge, a little lighter at the top. */
static void fire_gfx_fxHairline(FireGfxSurface* s, int x, int y, int w, int h, int corner, int dark) {
    FireGfxPixel edge, top;
    edge.index = top.index = 0;
    edge.rgba = dark ? 0x1CFFFFFFu : 0x1A000000u;
    top.rgba = dark ? 0x22FFFFFFu : 0x40FFFFFFu;
    fire_gfx_fxStrokeRoundRect(s, x, y, w, h, corner, 1, &edge);
    if (h > 2 * corner + 2) fire_gfx_fxSpan(s, y + 1, x + corner + 1, x + w - corner - 2, &top, 255);
}

/* Glass (kind 1) or acrylic (2) - or a plain tint (0) - in the rounded rectangle. `tint`: the color over the background (its alpha: how much of it; glass 10-20 %,
 * acrylic 40-60 %); `base`: the opaque color under the material when there is no transparency. `flags`: FIRE_GFX_FX_BLUR (else no blur: glass becomes a translucent
 * tint, acrylic a denser one), FIRE_GFX_FX_TRANSPARENCY (else opaque: the tint over `base`), FIRE_GFX_FX_DARK (the hairline of a dark theme). */
static void fire_gfx_fxMaterial(FireGfxSurface* s, int x, int y, int w, int h, int corner, int kind, uint32_t tint, uint32_t base, int flags) {
    FireGfxPixel p;
    int dark = (flags & FIRE_GFX_FX_DARK) != 0;
    p.index = 0;
    if (w <= 0 || h <= 0) return;
    if (s->indices || !(flags & FIRE_GFX_FX_TRANSPARENCY)) {
        /* opaque: what the tint gives over the base */
        p.rgba = fire_gfx_mix(base | 0xFF000000u, tint);
        if (s->indices) p = fire_gfx_resolve(s, fire_gfx_paintRgba(p.rgba));
        fire_gfx_fxFillRoundRect(s, x, y, w, h, corner, &p);
    } else if (kind == FIRE_GFX_MATERIAL_TINT || !(flags & FIRE_GFX_FX_BLUR) || !s->blend) {
        uint32_t a = tint >> 24;
        if (kind == FIRE_GFX_MATERIAL_GLASS) a = a + (255 - a) / 3;
        else if (kind == FIRE_GFX_MATERIAL_ACRYLIC) a = a + (255 - a) / 2;
        p.rgba = (tint & 0x00FFFFFFu) | (a << 24);
        fire_gfx_fxFillRoundRect(s, x, y, w, h, corner, &p);
    } else {
        int radius = kind == FIRE_GFX_MATERIAL_ACRYLIC ? 10 : 7;
        if (!fire_gfx_fxBlurMaterial(s, x, y, w, h, corner, radius, kind, tint, dark ? 10 : 18)) {
            p.rgba = tint; fire_gfx_fxFillRoundRect(s, x, y, w, h, corner, &p);
        }
    }
    if (!s->indices) fire_gfx_fxHairline(s, x, y, w, h, corner, dark);
}

#endif
