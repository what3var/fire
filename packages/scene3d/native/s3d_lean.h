/* fire scene3d: the lean renderer (RenderMode.Fast) - small, fast and frugal with memory, for old and small machines (DOS on 16 bit, 640x480 in the conventional memory) and
 * for anyone who wants speed more than the full picture.
 *
 * What it does differently from the software renderer (s3d_engine.h):
 *   - light per vertex (Gouraud: ambient, directional and point lights, diffuse only), not per pixel; no highlights, no bump maps;
 *   - shadows of one light (the first directional light that casts shadows): a small shadow map with 16-bit depths (S3D_LEAN_SHADOW_MAX texels on a side at most), hard
 *     edges; a pixel in the shadow loses that light's part of the vertex light;
 *   - the picture is drawn in bands of rows: the depth buffer (16 bits per pixel) and the colours exist for one band only, and a finished band goes straight to its target
 *     (the framebuffer's rows) - the memory follows the width of the picture, not its size;
 *   - textures are sampled nearest (no filtering), perspective correct every 16 pixels and linear between (the shadow map coordinates too);
 *   - a palette framebuffer is drawn in its palette: the texels become indices of it once, the light picks one of 32 shades of the colour from a table (S3D_LEAN_LEVELS);
 *   - transparency is a dither (a pixel is drawn or not, by a 4x4 pattern: no sorting), a texel with alpha below 128 (or below the cut-off) is not drawn.
 * Everything that is queued is kept as vertices on the screen and triangles of indices, not as set-up triangles; casters go into the shadow map as they are queued.
 * Plain C; the numbers on the way from the vertices to the rows are floats, the pixels are integers. The VM and a native build run the same code and give the same pixels, and
 * the size of the bands does not change them. */
#ifndef FIRE_S3D_LEAN_H
#define FIRE_S3D_LEAN_H

#ifndef FIRE_S3D_ENGINE_H
#include "s3d_engine.h"
#endif

#define S3D_LEAN_LEVELS 32        /* the shades of a colour in a palette picture; level 16 is the colour itself (a light of 1) */
#define S3D_LEAN_CHUNK 16         /* the pixels between two exact (perspective) texture coordinates */
#ifndef S3D_LEAN_BAND_BYTES
#if defined(_M_I86)
#define S3D_LEAN_BAND_BYTES 24576 /* the depth and the colours of one band (16 bit: a band in the conventional memory) */
#else
#define S3D_LEAN_BAND_BYTES 4194304
#endif
#endif
#ifndef S3D_LEAN_SHADOW_MAX
#if defined(_M_I86)
#define S3D_LEAN_SHADOW_MAX 128   /* 128 x 128 x 2 bytes = 32 KB */
#else
#define S3D_LEAN_SHADOW_MAX 1024
#endif
#endif
#define S3D_LEAN_ATTRS 14         /* 1/w, u/w, v/w, light r, g, b, level, the shadowed light's part r, g, b, level, shadow map u/w, v/w, depth/w */

/* A corner on the screen: position (pixels), 1/w, the texture coordinates divided by w, the light (r, g, b: 128 = 1, the base colour of the surface included; `level`: the shade
 * of a palette picture, the light alone), the part of it that the shadow light gives (sr, sg, sb, slevel) and the place in its shadow map (texels, depth units), divided by w. */
typedef struct S3dLeanVertex {
    float x, y, iw, uw, vw, suw, svw, sdw;
    uint8_t r, g, b, level, sr, sg, sb, slevel;
} S3dLeanVertex;
typedef struct S3dLeanTri { uint32_t a, b, c; uint16_t surface; int16_t minY, maxY; } S3dLeanTri;
/* What the triangles of one object share. */
typedef struct S3dLeanSurface {
    const S3dTexture* tex;
    uint8_t shadowed;       /* it receives the shadow */
    uint8_t baseIndex;      /* palette picture, no texture: the colour of the surface */
    uint8_t cutoff;         /* a texel with a smaller alpha is not drawn */
    uint8_t opacity;        /* 16: solid; below that the dither leaves pixels out */
} S3dLeanSurface;

/* The renderer of a camera (kept from picture to picture: its memory is used again). */
typedef struct S3dLean {
    int w, h, bandRows, bpp;
    uint16_t* depth;               /* w * bandRows */
    uint8_t* band;                 /* w * bandRows * bpp */
    s3d_size bandCap;
    S3dLeanVertex* v; s3d_size vN, vCap;
    S3dLeanTri* t; s3d_size tN, tCap;
    S3dLeanSurface* s; s3d_size sN, sCap;
    S3dMat4 vp;
    S3dCamera cam;
    float nearPlane;
    /* lights */
    S3dVec3 ambient;
    S3dPreparedLight* lights; s3d_size lightN, lightCap;
    int shadowLight;               /* the index of the light that casts the shadow, -1: none */
    /* its shadow map: the view of the light, u = x * scale + ox (texels), v = oy - y * scale, the depth along the light in units of 1/65535 of [dLo, dHi] */
    uint16_t* smap; int sres, sresCap;
    S3dMat4 sview;
    float sScale, sox, soy, sdLo, sdK, sTexel;
    int shadowsOn;
    /* a palette picture: its colours, the closest index of a colour (16 x 16 x 16), the shades */
    uint32_t pal[256];
    uint32_t palKey;               /* a hash of the palette: the tables and the textures' indices belong to it */
    int palReady;
    uint8_t* inverse;              /* 4096 */
    uint8_t* shade;                /* S3D_LEAN_LEVELS * 256 */
    uint32_t background;
} S3dLean;

static S3dLean* s3d_leanNew(void) { return (S3dLean*)s3d_calloc(1, sizeof(S3dLean)); }
static void s3d_leanFree(S3dLean* L) {
    if (L == NULL) return;
    S3D_FREE(L->depth); S3D_FREE(L->band); S3D_FREE(L->v); S3D_FREE(L->t); S3D_FREE(L->s); S3D_FREE(L->lights); S3D_FREE(L->inverse); S3D_FREE(L->shade);
    S3D_FREE(L->smap);
    S3D_FREE(L);
}

static int s3d_leanClampByte(float v) { return v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5f); }
static int s3d_leanClampLevel(float v) { return v <= 0 ? 0 : v >= S3D_LEAN_LEVELS - 1 ? S3D_LEAN_LEVELS - 1 : (int)(v + 0.5f); }

/* ---- the palette: the closest colour, the shades ---- */
static uint32_t s3d_leanPaletteKey(const uint32_t* pal) {
    uint32_t h = 2166136261u;
    int i;
    for (i = 0; i < 256; i++) { h = (h ^ (pal[i] & 0xFFFFFFu)) * 16777619u; }
    return h | 1u;   /* never 0: 0 is "no palette" */
}
static uint8_t s3d_leanNearest(const uint32_t* pal, int r, int g, int b) {
    int i, best = 0;
    long bestD = 0x7FFFFFFFL;
    for (i = 0; i < 256; i++) {
        long dr = (long)(pal[i] & 255) - r, dg = (long)((pal[i] >> 8) & 255) - g, db = (long)((pal[i] >> 16) & 255) - b;
        long d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
        if (d < bestD) { bestD = d; best = i; }
    }
    return (uint8_t)best;
}
static uint8_t s3d_leanIndexOf(const S3dLean* L, int r, int g, int b) { return L->inverse[((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4)]; }

/* The tables of a palette (only when it changed). 0 when the memory is gone. */
static int s3d_leanPalette(S3dLean* L, const uint32_t* pal) {
    uint32_t key = s3d_leanPaletteKey(pal);
    int i, l;
    if (L->palReady && key == L->palKey) return 1;
    if (L->inverse == NULL && (L->inverse = (uint8_t*)S3D_MALLOC(4096)) == NULL) return 0;
    if (L->shade == NULL && (L->shade = (uint8_t*)S3D_MALLOC(S3D_LEAN_LEVELS * 256)) == NULL) return 0;
    for (i = 0; i < 256; i++) L->pal[i] = pal[i];
    for (i = 0; i < 4096; i++) L->inverse[i] = s3d_leanNearest(pal, ((i >> 8) << 4) + 8, (((i >> 4) & 15) << 4) + 8, ((i & 15) << 4) + 8);
    for (l = 0; l < S3D_LEAN_LEVELS; l++)
        for (i = 0; i < 256; i++) {
            int r = (int)(pal[i] & 255) * l / 16, g = (int)((pal[i] >> 8) & 255) * l / 16, b = (int)((pal[i] >> 16) & 255) * l / 16;
            L->shade[l * 256 + i] = s3d_leanIndexOf(L, r > 255 ? 255 : r, g > 255 ? 255 : g, b > 255 ? 255 : b);
        }
    L->palKey = key;
    L->palReady = 1;
    return 1;
}

/* The texels of a texture as indices of the palette (made once per palette). */
static const uint8_t* s3d_leanTexIndices(S3dLean* L, S3dTexture* t) {
    s3d_size n = (s3d_size)t->w * (s3d_size)t->h, i;
    if (t->lean != NULL && t->leanKey == L->palKey) return t->lean;
    if (t->lean == NULL && (t->lean = (uint8_t*)S3D_MALLOC(n)) == NULL) return NULL;
    for (i = 0; i < n; i++) {
        const uint8_t* p = t->rgba + i * 4;
        t->lean[i] = s3d_leanIndexOf(L, p[0], p[1], p[2]);
    }
    t->leanKey = L->palKey;
    return t->lean;
}

/* ---- a picture: begin, shadows, objects, end ---- */
/* Starts a picture of w x h pixels, `bpp` 4 (RGBA) or 1 (indices of `palette`, 256 colours 0xAABBGGRR); `shadows`: the first directional light that casts shadows gets them.
 * 0 when the memory is gone. */
static int s3d_leanBegin(S3dLean* L, int w, int h, int bpp, const uint32_t* palette, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, uint32_t background, int shadows) {
    s3d_size i, rowBytes = (s3d_size)w * (s3d_size)(2 + bpp), rows;
    L->w = w; L->h = h; L->bpp = bpp;
    L->vN = L->tN = L->sN = 0;
    L->cam = *cam;
    L->nearPlane = cam->nearPlane > 1e-6f ? cam->nearPlane : 1e-6f;
    L->vp = s3d_viewProjection(cam, w, h);
    L->background = background;
    L->shadowsOn = 0;
    if (bpp == 1 && !s3d_leanPalette(L, palette)) return 0;
    rows = S3D_LEAN_BAND_BYTES / rowBytes;
    if (rows < 1) rows = 1;
    if (rows > (s3d_size)h) rows = (s3d_size)h;
    if (rows * rowBytes > L->bandCap || L->bandRows != (int)rows) {
        S3D_FREE(L->depth); S3D_FREE(L->band);
        L->depth = (uint16_t*)S3D_MALLOC((s3d_size)w * rows * 2);
        L->band = (uint8_t*)S3D_MALLOC((s3d_size)w * rows * (s3d_size)bpp);
        L->bandCap = L->depth && L->band ? rows * rowBytes : 0;
        if (L->bandCap == 0) { S3D_FREE(L->depth); S3D_FREE(L->band); L->depth = NULL; L->band = NULL; return 0; }
    }
    L->bandRows = (int)rows;
    /* the lights, as the software renderer prepares them */
    L->ambient = s3d_v3(0, 0, 0);
    L->lightN = 0;
    L->shadowLight = -1;
    for (i = 0; i < lightCount; i++) {
        const S3dLight* l = &lights[i];
        S3dPreparedLight p;
        if (l->kind == S3D_LIGHT_AMBIENT) { L->ambient = s3d_add(L->ambient, s3d_scale(l->color, l->intensity)); continue; }
        p.kind = l->kind;
        p.color = s3d_scale(l->color, l->intensity);
        p.pos = l->pos;
        p.toLight = s3d_normalize(s3d_scale(l->dir, -1.0f));
        p.invRange = 1.0f / (l->range > 1e-6f ? l->range : 1e-6f);
        p.mask = l->mask;
        p.shadow = -1;
        if (!S3D_GROW(L->lights, L->lightCap, L->lightN + 1)) return 0;
        if (shadows && L->shadowLight < 0 && l->castShadows && l->kind == S3D_LIGHT_DIRECTIONAL) L->shadowLight = (int)L->lightN;
        L->lights[L->lightN++] = p;
    }
    return 1;
}

/* The shadow map of the shadow light around the box lo..hi (the casters), `res` texels on a side (S3D_LEAN_SHADOW_MAX at most). Without it (no such light, no memory) there
 * are no shadows. */
static void s3d_leanShadows(S3dLean* L, S3dVec3 lo, S3dVec3 hi, int res) {
    const S3dPreparedLight* P;
    S3dVec3 forward, up, vlo = s3d_v3(1e30f, 1e30f, 1e30f), vhi = s3d_v3(-1e30f, -1e30f, -1e30f);
    float margin, extent;
    s3d_size i, n;
    int c;
    if (L->shadowLight < 0 || lo.x > hi.x) return;
    res = s3d_maxi(16, s3d_mini(S3D_LEAN_SHADOW_MAX, res));
    if (L->smap == NULL || L->sresCap < res) {
        S3D_FREE(L->smap);
        L->smap = (uint16_t*)S3D_MALLOC((s3d_size)res * (s3d_size)res * 2);
        L->sresCap = L->smap ? res : 0;
        if (L->smap == NULL) return;
    }
    P = &L->lights[L->shadowLight];
    forward = s3d_scale(P->toLight, -1.0f);
    up = fabsf(forward.y) > 0.99f ? s3d_v3(1, 0, 0) : s3d_v3(0, 1, 0);
    L->sview = s3d_lookAtMatrix(s3d_v3(0, 0, 0), forward, up);
    for (c = 0; c < 8; c++) {
        S3dVec3 q = s3d_transformPoint(&L->sview, s3d_v3((c & 1) ? hi.x : lo.x, (c & 2) ? hi.y : lo.y, (c & 4) ? hi.z : lo.z));
        vlo = s3d_v3(s3d_minf(vlo.x, q.x), s3d_minf(vlo.y, q.y), s3d_minf(vlo.z, q.z));
        vhi = s3d_v3(s3d_maxf(vhi.x, q.x), s3d_maxf(vhi.y, q.y), s3d_maxf(vhi.z, q.z));
    }
    margin = 0.05f * s3d_maxf(vhi.x - vlo.x, vhi.y - vlo.y) + 0.01f;
    vlo.x -= margin; vlo.y -= margin; vhi.x += margin; vhi.y += margin;
    extent = s3d_maxf(vhi.x - vlo.x, vhi.y - vlo.y);
    L->sres = res;
    L->sScale = (float)res / extent;
    L->sox = -vlo.x * L->sScale;
    L->soy = vhi.y * L->sScale;
    /* the depth along the light: d = -z of the view, from the nearest to the farthest corner (with a little room) */
    L->sdLo = -vhi.z - margin;
    L->sdK = 65535.0f / ((-vlo.z + margin) - L->sdLo);
    L->sTexel = extent / (float)res;
    n = (s3d_size)res * (s3d_size)res;
    for (i = 0; i < n; i++) L->smap[i] = 65535;
    L->shadowsOn = 1;
}

/* A corner in the shadow map: texels and depth units. */
static void s3d_leanShadowPoint(const S3dLean* L, S3dVec3 p, float* u, float* v, float* d) {
    S3dVec3 q = s3d_transformPoint(&L->sview, p);
    *u = q.x * L->sScale + L->sox;
    *v = L->soy - q.y * L->sScale;
    *d = (-q.z - L->sdLo) * L->sdK;
}

/* A triangle of a caster into the shadow map (the nearest depth stays). */
static void s3d_leanShadowTri(S3dLean* L, const float* a, const float* b, const float* c) {
    float minX = s3d_min3f(a[0], b[0], c[0]), maxX = s3d_max3f(a[0], b[0], c[0]), minY = s3d_min3f(a[1], b[1], c[1]), maxY = s3d_max3f(a[1], b[1], c[1]);
    float area = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1]), inv;
    int x0 = s3d_maxi(0, (int)ceilf(minX - 0.5f)), x1 = s3d_mini(L->sres - 1, (int)ceilf(maxX - 0.5f) - 1);
    int y0 = s3d_maxi(0, (int)ceilf(minY - 0.5f)), y1 = s3d_mini(L->sres - 1, (int)ceilf(maxY - 0.5f) - 1), x, y;
    if (area == 0 || x0 > x1 || y0 > y1) return;
    inv = 1.0f / area;
    for (y = y0; y <= y1; y++) {
        float py = (float)y + 0.5f;
        uint16_t* row = L->smap + (s3d_size)y * (s3d_size)L->sres;
        for (x = x0; x <= x1; x++) {
            float px = (float)x + 0.5f;
            float w0 = ((b[0] - px) * (c[1] - py) - (c[0] - px) * (b[1] - py)) * inv;
            float w1 = ((c[0] - px) * (a[1] - py) - (a[0] - px) * (c[1] - py)) * inv;
            float w2 = 1.0f - w0 - w1, d;
            int32_t q;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            d = w0 * a[2] + w1 * b[2] + w2 * c[2];
            q = d <= 0 ? 0 : d >= 65535 ? 65535 : (int32_t)d;
            if ((uint16_t)q < row[x]) row[x] = (uint16_t)q;
        }
    }
}

/* A clipped corner: clip space position and what is interpolated. */
typedef struct S3dLeanClip { float x, y, z, w, u, v, r, g, b, level, sr, sg, sb, slevel, su, sv, sd; } S3dLeanClip;

static uint32_t s3d_leanEmit(S3dLean* L, const S3dLeanClip* c) {
    S3dLeanVertex* o;
    float iw = 1.0f / c->w;
    if (!S3D_GROW(L->v, L->vCap, L->vN + 1)) return 0xFFFFFFFFu;
    o = &L->v[L->vN];
    o->x = (c->x * iw * 0.5f + 0.5f) * (float)L->w;
    o->y = (1.0f - (c->y * iw * 0.5f + 0.5f)) * (float)L->h;
    o->iw = iw; o->uw = c->u * iw; o->vw = c->v * iw;
    o->suw = c->su * iw; o->svw = c->sv * iw; o->sdw = c->sd * iw;
    o->r = (uint8_t)s3d_leanClampByte(c->r); o->g = (uint8_t)s3d_leanClampByte(c->g); o->b = (uint8_t)s3d_leanClampByte(c->b);
    o->level = (uint8_t)s3d_leanClampLevel(c->level);
    o->sr = (uint8_t)s3d_leanClampByte(c->sr); o->sg = (uint8_t)s3d_leanClampByte(c->sg); o->sb = (uint8_t)s3d_leanClampByte(c->sb);
    o->slevel = (uint8_t)s3d_leanClampLevel(c->slevel);
    return (uint32_t)L->vN++;
}

static void s3d_leanTriangle(S3dLean* L, uint32_t a, uint32_t b, uint32_t c, uint16_t surface) {
    const S3dLeanVertex *va = &L->v[a], *vb = &L->v[b], *vc = &L->v[c];
    float lo = s3d_min3f(va->y, vb->y, vc->y), hi = s3d_max3f(va->y, vb->y, vc->y);
    S3dLeanTri* t;
    if (hi < 0 || lo > (float)L->h) return;
    if (!S3D_GROW(L->t, L->tCap, L->tN + 1)) return;
    t = &L->t[L->tN++];
    t->a = a; t->b = b; t->c = c; t->surface = surface;
    t->minY = (int16_t)(lo < 0 ? 0 : lo > 32000 ? 32000 : (int)lo);
    t->maxY = (int16_t)(hi > 32000 ? 32000 : (int)hi + 1);
}

static S3dLeanClip s3d_leanLerp(const S3dLeanClip* p, const S3dLeanClip* q, float t) {
    S3dLeanClip r;
    const float* a = &p->x;
    const float* b = &q->x;
    float* o = &r.x;
    int k;
    for (k = 0; k < (int)(sizeof(S3dLeanClip) / sizeof(float)); k++) o[k] = s3d_lerp(a[k], b[k], t);
    return r;
}

/* Queues an object: its corners are transformed and lit once, its triangles cut against the near plane and kept as indices (and drawn into the shadow map, a caster). */
static void s3d_leanObject(S3dLean* L, const S3dMesh* mesh, const S3dObjectParams* obj, const S3dSurface* surf, S3dTexture* tex) {
    S3dMat4 model, normalM;
    S3dLeanSurface ls;
    S3dLeanClip* cv;
    s3d_size n = mesh->posN / 3, i, k;
    float tw = 1, th = 1;
    int textured = surf->useTexture && tex && tex->w > 0 && tex->h > 0 && tex->rgba;
    int casts, receives;
    uint16_t surface;
    if (L->w <= 0 || mesh->idxN < 3 || n == 0 || L->sN >= 65535) return;
    if (textured && L->bpp == 1 && s3d_leanTexIndices(L, tex) == NULL) textured = 0;
    s3d_objectMatrices(&L->cam, obj, surf, &model, &normalM);
    ls.tex = textured ? tex : NULL;
    ls.baseIndex = L->bpp == 1 ? s3d_leanIndexOf(L, s3d_leanClampByte(surf->color.x * 255), s3d_leanClampByte(surf->color.y * 255), s3d_leanClampByte(surf->color.z * 255)) : 0;
    ls.cutoff = (uint8_t)(surf->cutoff > 0 ? s3d_leanClampByte(surf->cutoff * 255) : (textured && tex->hasAlpha ? 128 : 0));
    ls.opacity = (uint8_t)(surf->opacity >= 1 ? 16 : surf->opacity <= 0 ? 0 : (int)(surf->opacity * 16 + 0.5f));
    casts = L->shadowsOn && surf->castShadows && ls.opacity >= 16;
    receives = L->shadowsOn && surf->receiveShadows && surf->lit && (L->lights[L->shadowLight].mask & surf->layers);
    ls.shadowed = (uint8_t)receives;
    if (ls.opacity == 0) return;
    if (!S3D_GROW(L->s, L->sCap, L->sN + 1)) return;
    surface = (uint16_t)L->sN;
    L->s[L->sN++] = ls;
    if (textured) { tw = (float)tex->w; th = (float)tex->h; }
    cv = (S3dLeanClip*)S3D_MALLOC(n * sizeof(S3dLeanClip));
    if (cv == NULL) return;
    for (i = 0; i < n; i++) {
        S3dVec3 wp = s3d_transformPoint(&model, s3d_v3(mesh->pos[i * 3], mesh->pos[i * 3 + 1], mesh->pos[i * 3 + 2]));
        S3dVec3 wn = s3d_normalize(s3d_transformDir(&normalM, s3d_v3(mesh->nrm[i * 3], mesh->nrm[i * 3 + 1], mesh->nrm[i * 3 + 2])));
        S3dLeanClip* c = &cv[i];
        float u = mesh->uv[i * 2], v = mesh->uv[i * 2 + 1];
        S3dVec3 light = L->ambient, sun = s3d_v3(0, 0, 0), col;
        float em = (surf->emissive.x + surf->emissive.y + surf->emissive.z) / 3;
        if (surf->twoSided && s3d_dot(wn, s3d_sub(L->cam.pos, wp)) < 0) wn = s3d_scale(wn, -1.0f);
        if (surf->lit) {
            for (k = 0; k < L->lightN; k++) {
                const S3dPreparedLight* P = &L->lights[k];
                S3dVec3 toLight, part;
                float atten = 1.0f, nl;
                if (!(P->mask & surf->layers)) continue;
                if (P->kind == S3D_LIGHT_DIRECTIONAL) toLight = P->toLight;
                else {
                    S3dVec3 d = s3d_sub(P->pos, wp);
                    float dist = s3d_length(d), q;
                    toLight = dist > 1e-12f ? s3d_scale(d, 1.0f / dist) : s3d_v3(0, 1, 0);
                    q = dist * P->invRange;
                    atten = 1.0f / (1.0f + q * q);
                }
                nl = s3d_dot(wn, toLight);
                if (nl <= 0) continue;
                part = s3d_scale(P->color, nl * atten);
                light = s3d_add(light, part);
                if ((int)k == L->shadowLight) sun = part;
            }
        } else light = s3d_v3(1, 1, 1);
        col = s3d_v3(surf->color.x * light.x + surf->emissive.x, surf->color.y * light.y + surf->emissive.y, surf->color.z * light.z + surf->emissive.z);
        c->r = col.x * 128; c->g = col.y * 128; c->b = col.z * 128;
        c->level = (light.x * 0.3f + light.y * 0.59f + light.z * 0.11f + em) * 16;
        c->sr = surf->color.x * sun.x * 128; c->sg = surf->color.y * sun.y * 128; c->sb = surf->color.z * sun.z * 128;
        c->slevel = (sun.x * 0.3f + sun.y * 0.59f + sun.z * 0.11f) * 16;
        c->su = c->sv = c->sd = 0;
        if (receives) {
            /* moved along its normal by about a texel: a surface does not shadow itself */
            s3d_leanShadowPoint(L, s3d_add(wp, s3d_scale(wn, L->sTexel * 1.5f)), &c->su, &c->sv, &c->sd);
            c->sd -= (L->sTexel + 0.002f) * L->sdK;
        }
        c->u = (surf->tm[0] * u + surf->tm[1] * v + surf->tm[2]) * tw;
        c->v = (surf->tm[3] * u + surf->tm[4] * v + surf->tm[5]) * th;
        c->x = S3D_AT(L->vp, 0, 0) * wp.x + S3D_AT(L->vp, 0, 1) * wp.y + S3D_AT(L->vp, 0, 2) * wp.z + S3D_AT(L->vp, 0, 3);
        c->y = S3D_AT(L->vp, 1, 0) * wp.x + S3D_AT(L->vp, 1, 1) * wp.y + S3D_AT(L->vp, 1, 2) * wp.z + S3D_AT(L->vp, 1, 3);
        c->z = S3D_AT(L->vp, 2, 0) * wp.x + S3D_AT(L->vp, 2, 1) * wp.y + S3D_AT(L->vp, 2, 2) * wp.z + S3D_AT(L->vp, 2, 3);
        c->w = S3D_AT(L->vp, 3, 0) * wp.x + S3D_AT(L->vp, 3, 1) * wp.y + S3D_AT(L->vp, 3, 2) * wp.z + S3D_AT(L->vp, 3, 3);
    }
    if (casts) {
        /* the casters' triangles in the shadow map (both sides: a closed mesh shadows with its back as well) */
        float* sp = (float*)S3D_MALLOC(n * 3 * sizeof(float));
        if (sp != NULL) {
            for (i = 0; i < n; i++) {
                S3dVec3 wp = s3d_transformPoint(&model, s3d_v3(mesh->pos[i * 3], mesh->pos[i * 3 + 1], mesh->pos[i * 3 + 2]));
                s3d_leanShadowPoint(L, wp, &sp[i * 3], &sp[i * 3 + 1], &sp[i * 3 + 2]);
            }
            for (k = 0; k + 2 < mesh->idxN; k += 3) {
                uint32_t i0 = mesh->idx[k], i1 = mesh->idx[k + 1], i2 = mesh->idx[k + 2];
                if (i0 < n && i1 < n && i2 < n) s3d_leanShadowTri(L, &sp[i0 * 3], &sp[i1 * 3], &sp[i2 * 3]);
            }
            S3D_FREE(sp);
        }
    }
    for (k = 0; k + 2 < mesh->idxN; k += 3) {
        uint32_t i0 = mesh->idx[k], i1 = mesh->idx[k + 1], i2 = mesh->idx[k + 2];
        const S3dLeanClip* in[3];
        S3dLeanClip poly[4];
        uint32_t out[4];
        int count = 0, e, j;
        if (i0 >= n || i1 >= n || i2 >= n) continue;
        in[0] = &cv[i0]; in[1] = &cv[i1]; in[2] = &cv[i2];
        /* the near plane (z >= -w); the other sides are cut by the rows and columns */
        for (e = 0; e < 3; e++) {
            const S3dLeanClip *cur = in[e], *nxt = in[(e + 1) % 3];
            float dc = cur->z + cur->w, dn = nxt->z + nxt->w;
            if (dc >= 0) poly[count++] = *cur;
            if ((dc >= 0) != (dn >= 0)) poly[count++] = s3d_leanLerp(cur, nxt, dc / (dc - dn));
        }
        if (count < 3) continue;
        for (j = 0; j < count; j++) if ((out[j] = s3d_leanEmit(L, &poly[j])) == 0xFFFFFFFFu) break;
        if (j < count) continue;
        /* facing: the winding on the screen (counter clockwise seen from outside = clockwise with y down) */
        {
            const S3dLeanVertex *a = &L->v[out[0]], *b = &L->v[out[1]], *c = &L->v[out[2]];
            float area = (b->x - a->x) * (c->y - a->y) - (c->x - a->x) * (b->y - a->y);
            if (area == 0 || (area > 0 && !surf->twoSided)) { L->vN -= (s3d_size)count; continue; }
        }
        for (j = 1; j + 1 < count; j++) s3d_leanTriangle(L, out[0], out[j], out[j + 1], surface);
    }
    S3D_FREE(cv);
}

/* ---- the rows ---- */
static const uint8_t s3d_leanBayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

/* The planes of the attributes of a triangle: value = base + gx * (x - x0) + gy * (y - y0). */
typedef struct S3dLeanPlanes { float x0, y0, base[S3D_LEAN_ATTRS], gx[S3D_LEAN_ATTRS], gy[S3D_LEAN_ATTRS]; } S3dLeanPlanes;

static void s3d_leanAttrs(const S3dLeanVertex* v, float* a) {
    a[0] = v->iw; a[1] = v->uw; a[2] = v->vw; a[3] = v->r; a[4] = v->g; a[5] = v->b; a[6] = v->level;
    a[7] = v->sr; a[8] = v->sg; a[9] = v->sb; a[10] = v->slevel; a[11] = v->suw; a[12] = v->svw; a[13] = v->sdw;
}
static int s3d_leanPlanes(const S3dLeanVertex* a, const S3dLeanVertex* b, const S3dLeanVertex* c, int count, S3dLeanPlanes* p) {
    float dx1 = b->x - a->x, dy1 = b->y - a->y, dx2 = c->x - a->x, dy2 = c->y - a->y;
    float det = dx1 * dy2 - dx2 * dy1, inv;
    float va[S3D_LEAN_ATTRS], vb[S3D_LEAN_ATTRS], vc[S3D_LEAN_ATTRS];
    int k;
    if (det == 0) return 0;
    inv = 1.0f / det;
    s3d_leanAttrs(a, va); s3d_leanAttrs(b, vb); s3d_leanAttrs(c, vc);
    p->x0 = a->x; p->y0 = a->y;
    for (k = 0; k < count; k++) {
        float d1 = vb[k] - va[k], d2 = vc[k] - va[k];
        p->base[k] = va[k];
        p->gx[k] = (d1 * dy2 - d2 * dy1) * inv;
        p->gy[k] = (d2 * dx1 - d1 * dx2) * inv;
    }
    return 1;
}

#define S3D_LEAN_AT(P, k, dx, dy) ((P).base[k] + (P).gx[k] * (dx) + (P).gy[k] * (dy))

/* One triangle into the rows y0..y1 of the band (band row 0 = picture row `top`). */
static void s3d_leanRaster(S3dLean* L, const S3dLeanTri* t, int top, int y0, int y1) {
    const S3dLeanVertex* vs[3];
    const S3dLeanSurface* s = &L->s[t->surface];
    const S3dTexture* tex = s->tex;
    const uint8_t* texIdx = tex && L->bpp == 1 ? tex->lean : NULL;
    int shadowed = s->shadowed && L->shadowsOn;
    S3dLeanPlanes P;
    int y, e, tw = tex ? tex->w : 1, th = tex ? tex->h : 1, sres = L->sres;
    int powU = (tw & (tw - 1)) == 0, powV = (th & (th - 1)) == 0;
    float depthScale = L->nearPlane * 65535.0f;
    vs[0] = &L->v[t->a]; vs[1] = &L->v[t->b]; vs[2] = &L->v[t->c];
    if (!s3d_leanPlanes(vs[0], vs[1], vs[2], shadowed ? S3D_LEAN_ATTRS : 7, &P)) return;
    for (y = y0; y <= y1; y++) {
        float py = (float)y + 0.5f, xl = 1e30f, xr = -1e30f, dx0, dy0;
        int xs, xe, crossings = 0, x;
        float iw, uw, vw, suw = 0, svw = 0, sdw = 0;
        int32_t r, g, b, lv, dr, dg, db, dlv, sr = 0, sg = 0, sb = 0, slv = 0, dsr = 0, dsg = 0, dsb = 0, dslv = 0;
        uint16_t* zrow = L->depth + (s3d_size)(y - top) * (s3d_size)L->w;
        uint8_t* crow = L->band + (s3d_size)(y - top) * (s3d_size)L->w * (s3d_size)L->bpp;
        for (e = 0; e < 3; e++) {
            const S3dLeanVertex *p = vs[e], *q = vs[(e + 1) % 3];
            float lo, hi, cx;
            if (p->y == q->y) continue;
            lo = p->y < q->y ? p->y : q->y; hi = p->y < q->y ? q->y : p->y;
            if (py < lo || py >= hi) continue;
            cx = p->x + (py - p->y) * (q->x - p->x) / (q->y - p->y);
            if (cx < xl) xl = cx;
            if (cx > xr) xr = cx;
            crossings++;
        }
        if (crossings < 2) continue;
        xs = s3d_maxi(0, (int)ceilf(xl - 0.5f)); xe = s3d_mini(L->w - 1, (int)ceilf(xr - 0.5f) - 1);
        if (xs > xe) continue;
        dx0 = (float)xs + 0.5f - P.x0; dy0 = py - P.y0;
        iw = S3D_LEAN_AT(P, 0, dx0, dy0); uw = S3D_LEAN_AT(P, 1, dx0, dy0); vw = S3D_LEAN_AT(P, 2, dx0, dy0);
        /* the light: 8.8 fixed point along the row */
        r = (int32_t)(S3D_LEAN_AT(P, 3, dx0, dy0) * 256); dr = (int32_t)(P.gx[3] * 256);
        g = (int32_t)(S3D_LEAN_AT(P, 4, dx0, dy0) * 256); dg = (int32_t)(P.gx[4] * 256);
        b = (int32_t)(S3D_LEAN_AT(P, 5, dx0, dy0) * 256); db = (int32_t)(P.gx[5] * 256);
        lv = (int32_t)(S3D_LEAN_AT(P, 6, dx0, dy0) * 256); dlv = (int32_t)(P.gx[6] * 256);
        if (shadowed) {
            sr = (int32_t)(S3D_LEAN_AT(P, 7, dx0, dy0) * 256); dsr = (int32_t)(P.gx[7] * 256);
            sg = (int32_t)(S3D_LEAN_AT(P, 8, dx0, dy0) * 256); dsg = (int32_t)(P.gx[8] * 256);
            sb = (int32_t)(S3D_LEAN_AT(P, 9, dx0, dy0) * 256); dsb = (int32_t)(P.gx[9] * 256);
            slv = (int32_t)(S3D_LEAN_AT(P, 10, dx0, dy0) * 256); dslv = (int32_t)(P.gx[10] * 256);
            suw = S3D_LEAN_AT(P, 11, dx0, dy0); svw = S3D_LEAN_AT(P, 12, dx0, dy0); sdw = S3D_LEAN_AT(P, 13, dx0, dy0);
        }
        for (x = xs; x <= xe; ) {
            /* a chunk: depth linear, texture and shadow map coordinates exact at both ends and linear between */
            int n = xe - x + 1, i;
            float iwEnd;
            int32_t z, dz, U = 0, V = 0, dU = 0, dV = 0, bu = 0, bv = 0, SU = 0, SV = 0, SD = 0, dSU = 0, dSV = 0, dSD = 0;
            if (n > S3D_LEAN_CHUNK) n = S3D_LEAN_CHUNK;
            iwEnd = iw + P.gx[0] * (float)n;
            z = (int32_t)(iw * depthScale * 256); dz = (int32_t)(P.gx[0] * depthScale * 256);
            if (tex) {
                float uwEnd = uw + P.gx[1] * (float)n, vwEnd = vw + P.gx[2] * (float)n;
                float u0 = uw / iw, v0 = vw / iw, u1 = uwEnd / iwEnd, v1 = vwEnd / iwEnd, fu = floorf(u0), fv = floorf(v0);
                bu = (int32_t)fu % tw; if (bu < 0) bu += tw;
                bv = (int32_t)fv % th; if (bv < 0) bv += th;
                U = (int32_t)((u0 - fu) * 65536); V = (int32_t)((v0 - fv) * 65536);
                dU = (int32_t)((u1 - u0) * 65536 / (float)n); dV = (int32_t)((v1 - v0) * 65536 / (float)n);
                uw = uwEnd; vw = vwEnd;
            }
            if (shadowed) {
                float suEnd = suw + P.gx[11] * (float)n, svEnd = svw + P.gx[12] * (float)n, sdEnd = sdw + P.gx[13] * (float)n;
                float a0 = suw / iw, b0 = svw / iw, c0 = sdw / iw, a1 = suEnd / iwEnd, b1 = svEnd / iwEnd, c1 = sdEnd / iwEnd;
                SU = (int32_t)(a0 * 65536); SV = (int32_t)(b0 * 65536); SD = (int32_t)(c0 * 256);
                dSU = (int32_t)((a1 - a0) * 65536 / (float)n); dSV = (int32_t)((b1 - b0) * 65536 / (float)n); dSD = (int32_t)((c1 - c0) * 256 / (float)n);
                suw = suEnd; svw = svEnd; sdw = sdEnd;
            }
            for (i = 0; i < n; i++, x++) {
                int32_t zz = z >> 8;
                uint16_t d = (uint16_t)(zz > 65535 ? 65535 : zz < 1 ? 1 : zz);
                if (d > zrow[x] && (s->opacity >= 16 || s3d_leanBayer[((y & 3) << 2) | (x & 3)] < s->opacity)) {
                    int32_t cr = 255, cg = 255, cb = 255, lr = r, lg = g, lb = b, ll = lv;
                    int idx = s->baseIndex, draw = 1;
                    if (tex) {
                        int32_t tx = bu + (U >> 16), ty = bv + (V >> 16);
                        s3d_size ti;
                        tx = powU ? (tx & (tw - 1)) : ((tx % tw) + tw) % tw;
                        ty = powV ? (ty & (th - 1)) : ((ty % th) + th) % th;
                        ti = (s3d_size)ty * (s3d_size)tw + (s3d_size)tx;
                        if (s->cutoff && tex->rgba[ti * 4 + 3] < s->cutoff) draw = 0;
                        if (texIdx) idx = texIdx[ti];
                        else { cr = tex->rgba[ti * 4]; cg = tex->rgba[ti * 4 + 1]; cb = tex->rgba[ti * 4 + 2]; }
                    }
                    if (draw && shadowed) {
                        int32_t mu = SU >> 16, mv = SV >> 16;
                        if (mu >= 0 && mv >= 0 && mu < sres && mv < sres && (int32_t)L->smap[(s3d_size)mv * (s3d_size)sres + (s3d_size)mu] * 256 < SD) {
                            lr -= sr; lg -= sg; lb -= sb; ll -= slv;   /* in the shadow: without the light's part */
                        }
                    }
                    if (draw) {
                        zrow[x] = d;
                        if (L->bpp == 1) {
                            int level = (int)(ll >> 8);
                            crow[x] = L->shade[(level < 0 ? 0 : level >= S3D_LEAN_LEVELS ? S3D_LEAN_LEVELS - 1 : level) * 256 + idx];
                        } else {
                            int32_t pr = (cr * (lr >> 8)) >> 7, pg = (cg * (lg >> 8)) >> 7, pb = (cb * (lb >> 8)) >> 7;
                            uint8_t* px = crow + (s3d_size)x * 4;
                            px[0] = (uint8_t)(pr > 255 ? 255 : pr < 0 ? 0 : pr); px[1] = (uint8_t)(pg > 255 ? 255 : pg < 0 ? 0 : pg); px[2] = (uint8_t)(pb > 255 ? 255 : pb < 0 ? 0 : pb); px[3] = 255;
                        }
                    }
                }
                z += dz; U += dU; V += dV; r += dr; g += dg; b += db; lv += dlv;
                sr += dsr; sg += dsg; sb += dsb; slv += dslv; SU += dSU; SV += dSV; SD += dSD;
            }
            iw = iwEnd;
        }
    }
}

/* Where the rows go: `rows` rows from picture row `y` (w * rows * bpp bytes). */
typedef void (*S3dLeanSink)(void* ctx, int y, int rows, const uint8_t* data);

/* Draws what was queued, band by band, and hands every finished band to `sink`. */
static void s3d_leanEnd(S3dLean* L, S3dLeanSink sink, void* ctx) {
    int top;
    uint8_t bg[4];
    uint8_t bgIndex = 0;
    bg[0] = (uint8_t)(L->background & 255); bg[1] = (uint8_t)((L->background >> 8) & 255); bg[2] = (uint8_t)((L->background >> 16) & 255); bg[3] = (uint8_t)((L->background >> 24) & 255);
    if (L->bpp == 1) bgIndex = s3d_leanIndexOf(L, bg[0], bg[1], bg[2]);
    for (top = 0; top < L->h; top += L->bandRows) {
        int rows = s3d_mini(L->bandRows, L->h - top), bottom = top + rows - 1;
        s3d_size i, n = (s3d_size)L->w * (s3d_size)rows;
        for (i = 0; i < n; i++) L->depth[i] = 0;
        if (L->bpp == 1) for (i = 0; i < n; i++) L->band[i] = bgIndex;
        else for (i = 0; i < n; i++) { L->band[i * 4] = bg[0]; L->band[i * 4 + 1] = bg[1]; L->band[i * 4 + 2] = bg[2]; L->band[i * 4 + 3] = bg[3]; }
        for (i = 0; i < L->tN; i++) {
            const S3dLeanTri* t = &L->t[i];
            if (t->maxY < top || t->minY > bottom) continue;
            s3d_leanRaster(L, t, top, s3d_maxi(t->minY, top), s3d_mini(t->maxY, bottom));
        }
        sink(ctx, top, rows, L->band);
    }
    L->vN = L->tN = L->sN = 0;
}

#endif /* FIRE_S3D_LEAN_H */
