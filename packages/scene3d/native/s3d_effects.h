/* fire scene3d: particles and the effects on the whole picture (scene3d.effects, docs/graphics/effects.md).
 *
 * Particles: an emitter has a pool of a fixed size (no memory per particle and picture). It makes particles at its place (in a box around it, `area`), sends them
 * in a cone around a direction, and moves them on with gravity and drag; a particle lives for its lifetime and goes from its first colour and size to its last.
 * Its chance comes from its own generator (seed): the same steps give the same particles in the VM and in a native build.
 * They are drawn after the triangles, into the finished pixels, as soft discs that face the camera - tested against the depth of the picture, not written
 * into it - added (fire, sparks: they light up) or blended (smoke, rain, snow). The lean renderer does it band by band, also into a palette picture
 * (the colour that comes out is looked up in the palette).
 *
 * Effects on the whole picture: fog (from a distance on, the picture goes into the fog colour; what no object covers is fog) and a tint (the whole picture
 * towards a colour: fading out, flashes, under water). */
#ifndef FIRE_S3D_EFFECTS_H
#define FIRE_S3D_EFFECTS_H

#ifndef FIRE_S3D_ENGINE_H
#include "s3d_engine.h"
#endif

/* the settings of an emitter, as the bridge sets them by number */
enum {
    S3D_EM_RATE = 0, S3D_EM_LIFE, S3D_EM_LIFEVAR, S3D_EM_SPEED, S3D_EM_SPEEDVAR, S3D_EM_SPREAD, S3D_EM_DIRX, S3D_EM_DIRY, S3D_EM_DIRZ,
    S3D_EM_GRAVX, S3D_EM_GRAVY, S3D_EM_GRAVZ, S3D_EM_DRAG, S3D_EM_SIZE0, S3D_EM_SIZE1, S3D_EM_AREAX, S3D_EM_AREAY, S3D_EM_AREAZ,
    S3D_EM_MAX, S3D_EM_ADDITIVE, S3D_EM_RUNNING, S3D_EM_COUNT_SETTINGS
};

typedef struct S3dParticle { float x, y, z, vx, vy, vz, age, life; } S3dParticle;

typedef struct S3dEmitter {
    float st[S3D_EM_COUNT_SETTINGS];
    uint32_t color0, color1;   /* 0xAABBGGRR: at the start and at the end of a life */
    S3dParticle* p; int n; s3d_size cap;
    float due;                 /* particles that are due (the part of one that the rate made) */
    uint32_t seed;
} S3dEmitter;

static S3dEmitter* s3d_emitterNew(void) {
    S3dEmitter* e = (S3dEmitter*)s3d_calloc(1, sizeof(S3dEmitter));
    if (e == NULL) return NULL;
    e->st[S3D_EM_RATE] = 20; e->st[S3D_EM_LIFE] = 1.5f; e->st[S3D_EM_LIFEVAR] = 0.3f; e->st[S3D_EM_SPEED] = 1; e->st[S3D_EM_SPEEDVAR] = 0.3f;
    e->st[S3D_EM_SPREAD] = 0.4f; e->st[S3D_EM_DIRY] = 1; e->st[S3D_EM_SIZE0] = 0.1f; e->st[S3D_EM_SIZE1] = 0.05f; e->st[S3D_EM_MAX] = 200;
    e->st[S3D_EM_RUNNING] = 1;
    e->color0 = 0xFFFFFFFFu; e->color1 = 0x00FFFFFFu;
    e->seed = 0x2545F491u;
    return e;
}
static void s3d_emitterFree(S3dEmitter* e) { if (e) { S3D_FREE(e->p); S3D_FREE(e); } }

/* 0 .. 1 from the emitter's own generator (xorshift) */
static float s3d_rand(S3dEmitter* e) {
    uint32_t x = e->seed;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    e->seed = x;
    return (float)(x >> 8) / 16777216.0f;
}

static void s3d_emitterSpawn(S3dEmitter* e, S3dVec3 origin) {
    S3dParticle* p;
    S3dVec3 d = s3d_normalize(s3d_v3(e->st[S3D_EM_DIRX], e->st[S3D_EM_DIRY], e->st[S3D_EM_DIRZ])), a, b;
    float cosMax = cosf(e->st[S3D_EM_SPREAD]), ct, st, phi, speed;
    int max = e->st[S3D_EM_MAX] < 1 ? 1 : e->st[S3D_EM_MAX] > 16000 ? 16000 : (int)e->st[S3D_EM_MAX];   /* (an int of 16 bits holds it) */
    if (e->n >= max) return;
    if ((s3d_size)e->n >= e->cap && !S3D_GROW(e->p, e->cap, (s3d_size)e->n + 1)) return;
    p = &e->p[e->n++];
    p->x = origin.x + (s3d_rand(e) * 2 - 1) * e->st[S3D_EM_AREAX];
    p->y = origin.y + (s3d_rand(e) * 2 - 1) * e->st[S3D_EM_AREAY];
    p->z = origin.z + (s3d_rand(e) * 2 - 1) * e->st[S3D_EM_AREAZ];
    /* a direction in the cone around d: cos of the angle evenly between cos(spread) and 1, all around */
    ct = 1 - s3d_rand(e) * (1 - cosMax);
    st = sqrtf(s3d_maxf(0, 1 - ct * ct));
    phi = s3d_rand(e) * 6.2831853f;
    a = s3d_normalize(fabsf(d.y) > 0.9f ? s3d_cross(d, s3d_v3(1, 0, 0)) : s3d_cross(d, s3d_v3(0, 1, 0)));
    b = s3d_cross(d, a);
    speed = e->st[S3D_EM_SPEED] * (1 + (s3d_rand(e) * 2 - 1) * e->st[S3D_EM_SPEEDVAR]);
    {
        S3dVec3 v = s3d_add(s3d_scale(d, ct), s3d_add(s3d_scale(a, st * cosf(phi)), s3d_scale(b, st * sinf(phi))));
        p->vx = v.x * speed; p->vy = v.y * speed; p->vz = v.z * speed;
    }
    p->age = 0;
    p->life = e->st[S3D_EM_LIFE] * (1 + (s3d_rand(e) * 2 - 1) * e->st[S3D_EM_LIFEVAR]);
    if (p->life < 0.01f) p->life = 0.01f;
}

/* The ready emitters of scene3d.effects (kind 0 fire, 1 smoke, 2 sparks, 3 rain, 4 snow, 5 an explosion; `size`: the square of rain and snow, the particles of an
 * explosion): rate, life, its variation, speed, its variation, spread, direction, gravity, drag, sizes, area, max, additive, running - and the two colours. */
static const float s3d_presetSettings[6][S3D_EM_COUNT_SETTINGS] = {
    { 60, 0.8f, 0.3f, 1.2f, 0.3f, 0.35f, 0, 1, 0, 0, 0, 0, 0.5f, 0.35f, 0.05f, 0.15f, 0, 0.15f, 200, 1, 1 },
    { 12, 3, 0.3f, 0.6f, 0.3f, 0.26f, 0, 1, 0, 0, 0, 0, 0.3f, 0.3f, 1.2f, 0.1f, 0, 0.1f, 200, 0, 1 },
    { 40, 0.7f, 0.3f, 4, 0.3f, 0.61f, 0, 1, 0, 0, -9.81f, 0, 0, 0.06f, 0.02f, 0, 0, 0, 200, 1, 1 },
    { 300, 1.5f, 0.1f, 8, 0.1f, 0.017f, 0, -1, 0, 0, 0, 0, 0, 0.04f, 0.04f, 5, 0, 5, 600, 0, 1 },
    { 60, 6, 0.3f, 0.8f, 0.3f, 0.44f, 0, -1, 0, 0, 0, 0, 0, 0.08f, 0.08f, 5, 0, 5, 500, 0, 1 },
    { 0, 1, 0.3f, 5, 0.5f, 3.1415927f, 0, 1, 0, 0, 0, 0, 2, 0.5f, 0.1f, 0, 0, 0, 150, 1, 0 }
};
static const uint32_t s3d_presetColors[6][2] = {
    { 0xE63CC8FFu, 0x000A1EC8u }, { 0x966E6E6Eu, 0x00969696u }, { 0xFFA0F0FFu, 0x001478FFu },
    { 0x82E6BEAAu, 0x6EE6BEAAu }, { 0xE6FFFFFFu, 0xC8FFFFFFu }, { 0xFF78DCFFu, 0x00001EA0u }
};
static void s3d_emitterPreset(S3dEmitter* e, int kind, float size) {
    int i;
    if (kind < 0 || kind > 5) return;
    for (i = 0; i < S3D_EM_COUNT_SETTINGS; i++) e->st[i] = s3d_presetSettings[kind][i];
    e->color0 = s3d_presetColors[kind][0]; e->color1 = s3d_presetColors[kind][1];
    if ((kind == 3 || kind == 4) && size > 0) e->st[S3D_EM_AREAX] = e->st[S3D_EM_AREAZ] = size * 0.5f;
    if (kind == 5 && size >= 1) e->st[S3D_EM_MAX] = size;
}

/* `count` particles at once (an explosion). */
static void s3d_emitterBurst(S3dEmitter* e, S3dVec3 origin, int count) { int i; for (i = 0; i < count; i++) s3d_emitterSpawn(e, origin); }

/* The time goes on by dt seconds: the particles move and age, the old ones go, new ones are made (while it runs). */
static void s3d_emitterAdvance(S3dEmitter* e, S3dVec3 origin, float dt) {
    int i, k;
    float drag = 1 - e->st[S3D_EM_DRAG] * dt;
    if (dt <= 0) return;
    if (drag < 0) drag = 0;
    for (i = 0, k = 0; i < e->n; i++) {
        S3dParticle p = e->p[i];
        p.age += dt;
        if (p.age >= p.life) continue;
        p.vx = (p.vx + e->st[S3D_EM_GRAVX] * dt) * drag; p.vy = (p.vy + e->st[S3D_EM_GRAVY] * dt) * drag; p.vz = (p.vz + e->st[S3D_EM_GRAVZ] * dt) * drag;
        p.x += p.vx * dt; p.y += p.vy * dt; p.z += p.vz * dt;
        e->p[k++] = p;
    }
    e->n = k;
    if (e->st[S3D_EM_RUNNING] != 0 && e->st[S3D_EM_RATE] > 0) {
        e->due += e->st[S3D_EM_RATE] * dt;
        while (e->due >= 1) { e->due -= 1; s3d_emitterSpawn(e, origin); }
    }
}

/* ---- drawing ---- */
/* What the effects draw into: rows y0..y1 of a picture (`px`: the first of them; 4 bytes RGBA a pixel, or 1 byte: an index into `pal`, a colour back to an index
 * through `inverse` - 16 x 16 x 16, as the lean renderer has it), and the depth that was drawn there: the full renderer's (NDC z, 1e30 nothing) or the lean
 * one's (near * 65535 / distance, 0 nothing), or none (NULL: the GPU drew - particles in front of everything, no fog). */
typedef struct S3dFxTarget {
    uint8_t* px; int w, y0, y1, bpp;
    const uint32_t* pal; const uint8_t* inverse;
    const float* zdepth; float zA, zB, zC;      /* distance = zC / (z * zA + zB) */
    const uint16_t* qdepth; float qScale;      /* distance = qScale / q */
} S3dFxTarget;

static void s3d_fxTargetFull(S3dFxTarget* t, uint8_t* rgba, int w, int h, const float* depth, float nearP, float farP) {
    memset(t, 0, sizeof(*t));
    t->px = rgba; t->w = w; t->y0 = 0; t->y1 = h - 1; t->bpp = 4;
    t->zdepth = depth; t->zA = nearP - farP; t->zB = farP + nearP; t->zC = 2 * farP * nearP;
}

/* The distance of what is drawn at (x, y): 0 nothing (the background), -1 not known. */
static float s3d_fxDistance(const S3dFxTarget* t, int x, int y) {
    s3d_size i = (s3d_size)(y - t->y0) * (s3d_size)t->w + (s3d_size)x;
    if (t->qdepth) { uint16_t q = t->qdepth[i]; return q ? t->qScale / (float)q : 0; }
    if (t->zdepth) { float z = t->zdepth[i]; return z >= 1e29f ? 0 : t->zC / (z * t->zA + t->zB); }
    return -1;
}
static void s3d_fxGet(const S3dFxTarget* t, uint8_t* p, float* c) {
    if (t->bpp == 1) { uint32_t q = t->pal[*p]; c[0] = (float)(q & 255); c[1] = (float)((q >> 8) & 255); c[2] = (float)((q >> 16) & 255); }
    else { c[0] = p[0]; c[1] = p[1]; c[2] = p[2]; }
}
static void s3d_fxPut(const S3dFxTarget* t, uint8_t* p, const float* c) {
    int r = c[0] <= 0 ? 0 : c[0] >= 255 ? 255 : (int)(c[0] + 0.5f), g = c[1] <= 0 ? 0 : c[1] >= 255 ? 255 : (int)(c[1] + 0.5f), b = c[2] <= 0 ? 0 : c[2] >= 255 ? 255 : (int)(c[2] + 0.5f);
    if (t->bpp == 1) *p = t->inverse[((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4)];
    else { p[0] = (uint8_t)r; p[1] = (uint8_t)g; p[2] = (uint8_t)b; }
}
static uint8_t* s3d_fxAt(const S3dFxTarget* t, int x, int y) { return t->px + ((s3d_size)(y - t->y0) * (s3d_size)t->w + (s3d_size)x) * (s3d_size)t->bpp; }

/* A particle on the screen: where, how big, its colour (RGB 0..255 and alpha 0..1), how far (w). */
typedef struct S3dSplat { float sx, sy, radius, w, r, g, b, a; int additive; } S3dSplat;

/* 0: behind the camera, too small, transparent. vp: the camera's view and projection, tf = 1 / tan(fov / 2). */
static int s3d_splatOf(const S3dEmitter* e, const S3dParticle* p, const S3dMat4* vp, float tf, int w, int h, float nearPlane, S3dSplat* out) {
    float cx = S3D_AT(*vp, 0, 0) * p->x + S3D_AT(*vp, 0, 1) * p->y + S3D_AT(*vp, 0, 2) * p->z + S3D_AT(*vp, 0, 3);
    float cy = S3D_AT(*vp, 1, 0) * p->x + S3D_AT(*vp, 1, 1) * p->y + S3D_AT(*vp, 1, 2) * p->z + S3D_AT(*vp, 1, 3);
    float cw = S3D_AT(*vp, 3, 0) * p->x + S3D_AT(*vp, 3, 1) * p->y + S3D_AT(*vp, 3, 2) * p->z + S3D_AT(*vp, 3, 3);
    float t = p->age / p->life, size;
    uint32_t c0 = e->color0, c1 = e->color1;
    if (cw <= nearPlane) return 0;
    size = e->st[S3D_EM_SIZE0] + (e->st[S3D_EM_SIZE1] - e->st[S3D_EM_SIZE0]) * t;
    out->sx = (cx / cw * 0.5f + 0.5f) * (float)w;
    out->sy = (1.0f - (cy / cw * 0.5f + 0.5f)) * (float)h;
    out->radius = size * 0.5f * tf * (float)h * 0.5f / cw;
    out->w = cw;
    out->r = (float)(c0 & 255) + ((float)(c1 & 255) - (float)(c0 & 255)) * t;
    out->g = (float)((c0 >> 8) & 255) + ((float)((c1 >> 8) & 255) - (float)((c0 >> 8) & 255)) * t;
    out->b = (float)((c0 >> 16) & 255) + ((float)((c1 >> 16) & 255) - (float)((c0 >> 16) & 255)) * t;
    out->a = ((float)((c0 >> 24) & 255) + ((float)((c1 >> 24) & 255) - (float)((c0 >> 24) & 255)) * t) / 255.0f;
    out->additive = e->st[S3D_EM_ADDITIVE] != 0;
    return out->radius > 0.05f && out->a > 0.002f && out->sx + out->radius >= 0 && out->sx - out->radius <= (float)w && out->sy + out->radius >= 0 && out->sy - out->radius <= (float)h;
}

/* The fog over a distance: 0 none .. 1 all fog. */
static float s3d_fogFactor(float dist, float start, float end) {
    if (!(end > start)) return 0;
    return dist <= start ? 0 : dist >= end ? 1 : (dist - start) / (end - start);
}

/* The effects of a picture: the fog (end > start; colour 0xAABBGGRR), the tint (amount 0..1), the particles. The lean renderer's fog goes in S3D_FOG_LEVELS
 * steps without a division a pixel: the depth where each step begins (`fogQ`), and for a palette picture the index each colour becomes at each step (`fogMap`,
 * S3D_FOG_LEVELS * 256 bytes; made on the first band, the caller frees it). */
#define S3D_FOG_LEVELS 16
typedef struct S3dFx {
    float fogStart, fogEnd; uint32_t fogColor; float tint; uint32_t tintColor; const S3dSplat* splats; s3d_size splatN;
    uint16_t fogQ[S3D_FOG_LEVELS + 1]; int fogReady; uint8_t* fogMap;
} S3dFx;

/* The fog step of a lean depth: 0 none .. S3D_FOG_LEVELS all fog (0: nothing there - all fog). */
static int s3d_fogLevel(const S3dFx* fx, uint16_t q) {
    int k = 0;
    if (q == 0) return S3D_FOG_LEVELS;
    while (k < S3D_FOG_LEVELS && q < fx->fogQ[k]) k++;
    return k;
}
static void s3d_fogPrepare(const S3dFxTarget* t, S3dFx* fx) {
    int k, i;
    fx->fogReady = 1;
    for (k = 0; k <= S3D_FOG_LEVELS; k++) {   /* step k begins halfway between the distances of k - 1 and k */
        float d = fx->fogStart + (fx->fogEnd - fx->fogStart) * ((float)k - 0.5f) / (float)S3D_FOG_LEVELS, q = d > 0 ? t->qScale / d : 65535.0f;
        fx->fogQ[k] = (uint16_t)(q >= 65535.0f ? 65535 : q < 1 ? 1 : (int32_t)q);
    }
    if (t->bpp == 1 && (fx->fogMap = (uint8_t*)S3D_MALLOC((s3d_size)S3D_FOG_LEVELS * 256)) != NULL) {
        float fc[3], c[3];
        uint8_t p;
        fc[0] = (float)(fx->fogColor & 255); fc[1] = (float)((fx->fogColor >> 8) & 255); fc[2] = (float)((fx->fogColor >> 16) & 255);
        for (k = 1; k <= S3D_FOG_LEVELS; k++) for (i = 0; i < 256; i++) {
            float a = (float)k / (float)S3D_FOG_LEVELS;
            p = (uint8_t)i;
            s3d_fxGet(t, &p, c);
            c[0] += (fc[0] - c[0]) * a; c[1] += (fc[1] - c[1]) * a; c[2] += (fc[2] - c[2]) * a;
            s3d_fxPut(t, &fx->fogMap[(k - 1) * 256 + i], c);
        }
    }
}

/* One splat into the rows of the target, behind what is nearer: a soft disc (full in the middle, nothing at the edge), added (it lights up) or blended; the fog
 * is applied to the particle itself. */
static void s3d_splatDraw(const S3dFxTarget* t, const S3dSplat* s, const S3dFx* fx) {
    int xa = s3d_maxi(0, (int)floorf(s->sx - s->radius)), xb = s3d_mini(t->w - 1, (int)ceilf(s->sx + s->radius));
    int ya = s3d_maxi(t->y0, (int)floorf(s->sy - s->radius)), yb = s3d_mini(t->y1, (int)ceilf(s->sy + s->radius)), x, y;
    float inv = 1.0f / (s->radius * s->radius), fog = s3d_fogFactor(s->w, fx->fogStart, fx->fogEnd), c[3], q[3];
    uint32_t fc = fx->fogColor;
    q[0] = s->r + ((float)(fc & 255) - s->r) * fog; q[1] = s->g + ((float)((fc >> 8) & 255) - s->g) * fog; q[2] = s->b + ((float)((fc >> 16) & 255) - s->b) * fog;
    for (y = ya; y <= yb; y++) {
        float dy = (float)y + 0.5f - s->sy;
        for (x = xa; x <= xb; x++) {
            float dx = (float)x + 0.5f - s->sx, d = (dx * dx + dy * dy) * inv, k, behind;
            uint8_t* p;
            if (d >= 1) continue;
            behind = s3d_fxDistance(t, x, y);
            if (behind > 0 && behind <= s->w) continue;
            k = (s->additive ? s->a * (1 - fog) : s->a) * (1 - d);
            p = s3d_fxAt(t, x, y);
            s3d_fxGet(t, p, c);
            if (s->additive) { c[0] += q[0] * k; c[1] += q[1] * k; c[2] += q[2] * k; }
            else { c[0] += (q[0] - c[0]) * k; c[1] += (q[1] - c[1]) * k; c[2] += (q[2] - c[2]) * k; }
            s3d_fxPut(t, p, c);
        }
    }
}

/* All effects on the rows of the target: fog on what was drawn (what no object covers is all fog), the particles, the tint. */
static void s3d_fxApply(const S3dFxTarget* t, S3dFx* fx) {
    int x, y, fog = fx->fogEnd > fx->fogStart && (t->zdepth || t->qdepth);
    s3d_size i;
    float c[3], fc[3], tc[3];
    fc[0] = (float)(fx->fogColor & 255); fc[1] = (float)((fx->fogColor >> 8) & 255); fc[2] = (float)((fx->fogColor >> 16) & 255);
    tc[0] = (float)(fx->tintColor & 255); tc[1] = (float)((fx->tintColor >> 8) & 255); tc[2] = (float)((fx->tintColor >> 16) & 255);
    if (fog && t->qdepth) {   /* the lean renderer: in steps, integers only */
        int f0 = (int)fc[0], f1 = (int)fc[1], f2 = (int)fc[2];
        if (!fx->fogReady) s3d_fogPrepare(t, fx);
        for (y = t->y0; y <= t->y1; y++) {
            const uint16_t* q = t->qdepth + (s3d_size)(y - t->y0) * (s3d_size)t->w;
            uint8_t* p = s3d_fxAt(t, 0, y);
            for (x = 0; x < t->w; x++, p += t->bpp) {
                int k = s3d_fogLevel(fx, q[x]);
                if (k == 0) continue;
                if (t->bpp == 1) { if (fx->fogMap) *p = fx->fogMap[(k - 1) * 256 + *p]; }
                else {
                    p[0] = (uint8_t)(p[0] + (((f0 - p[0]) * k) / 16)); p[1] = (uint8_t)(p[1] + (((f1 - p[1]) * k) / 16)); p[2] = (uint8_t)(p[2] + (((f2 - p[2]) * k) / 16));
                }
            }
        }
    } else if (fog) {
        for (y = t->y0; y <= t->y1; y++) for (x = 0; x < t->w; x++) {
            float d = s3d_fxDistance(t, x, y), k = d <= 0 ? 1 : s3d_fogFactor(d, fx->fogStart, fx->fogEnd);
            uint8_t* p;
            if (k <= 0) continue;
            p = s3d_fxAt(t, x, y);
            if (k >= 1) { s3d_fxPut(t, p, fc); continue; }
            s3d_fxGet(t, p, c);
            c[0] += (fc[0] - c[0]) * k; c[1] += (fc[1] - c[1]) * k; c[2] += (fc[2] - c[2]) * k;
            s3d_fxPut(t, p, c);
        }
    }
    for (i = 0; i < fx->splatN; i++) {
        const S3dSplat* s = &fx->splats[i];
        if (s->sy + s->radius >= (float)t->y0 && s->sy - s->radius <= (float)t->y1 + 1) s3d_splatDraw(t, s, fx);
    }
    if (fx->tint > 0) {
        float k = fx->tint > 1 ? 1 : fx->tint;
        for (y = t->y0; y <= t->y1; y++) for (x = 0; x < t->w; x++) {
            uint8_t* p = s3d_fxAt(t, x, y);
            s3d_fxGet(t, p, c);
            c[0] += (tc[0] - c[0]) * k; c[1] += (tc[1] - c[1]) * k; c[2] += (tc[2] - c[2]) * k;
            s3d_fxPut(t, p, c);
        }
    }
}

/* The particles of emitters as splats, from far to near (blended ones over each other in the right order); NULL with *n = 0 when there are none (or no memory). */
static int s3d_splatOrder(const void* a, const void* b) {
    const S3dSplat* p = (const S3dSplat*)a; const S3dSplat* q = (const S3dSplat*)b;
    if (p->w != q->w) return p->w > q->w ? -1 : 1;
    if (p->sx != q->sx) return p->sx < q->sx ? -1 : 1;
    return p->sy < q->sy ? -1 : p->sy > q->sy ? 1 : 0;
}
static S3dSplat* s3d_splatsOf(S3dEmitter* const* es, s3d_size en, const S3dCamera* cam, int w, int h, s3d_size* n) {
    S3dMat4 vp = s3d_viewProjection(cam, w, h);
    float tf = 1.0f / tanf(cam->fovY * 0.5f);
    s3d_size total = 0, i;
    int k;
    S3dSplat* out;
    *n = 0;
    for (i = 0; i < en; i++) total += (s3d_size)es[i]->n;
    if (total == 0 || (out = (S3dSplat*)S3D_MALLOC(total * sizeof(S3dSplat))) == NULL) return NULL;
    for (i = 0; i < en; i++) for (k = 0; k < es[i]->n; k++) if (s3d_splatOf(es[i], &es[i]->p[k], &vp, tf, w, h, cam->nearPlane, &out[*n])) (*n)++;
    if (*n > 1) qsort(out, *n, sizeof(S3dSplat), s3d_splatOrder);
    return out;
}

#endif /* FIRE_S3D_EFFECTS_H */
