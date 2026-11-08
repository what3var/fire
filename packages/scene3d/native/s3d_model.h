/* fire scene3d: models - the compact model file of fire (.f3d, docs/graphics/models.md) and the poses of its animations.
 *
 * A model is a tree of parts. Every part has a place relative to its parent (translation, rotation as a quaternion, scale: its rest pose) and may have a mesh in its own
 * space; an animation (a clip) is a list of frames, and a frame holds the place of every part. Parts are rigid: a clip moves, turns and scales whole parts (the way
 * games of the 90s animated their figures - small, and cheap to compute); there are no bones that bend a mesh.
 *
 * The file (little endian; made by forge from glTF 2.0 - src/fire.Compiler/ModelConverter.cs - or by the editor):
 *   "F3D1"  u16 version (1)  u16 parts  u16 clips  u16 reserved  f32 box min[3] max[3] (the whole model in its rest pose)
 *   part:   i16 parent (-1: a root; a parent comes before its children)  u8 flags (1: it has a colour, 2: texture coordinates)  u8 name length, name (UTF-8)
 *           f32 t[3]  i16 q[4] (x, y, z, w / 32767)  f32 s[3]  u8 r g b a
 *           u16 vertices  u32 indices  f32 mesh box min[3] max[3]
 *           vertices: i16 x, y, z (the mesh box: min + (q + 32768) / 65535 * size)  i8 normal u, v (octahedral, / 127)  [i16 u, v (/ 1024): flag 2]
 *           indices: u16 each (three per triangle, counter clockwise seen from outside)
 *   clip:   u8 name length, name  u8 flags (1: loops)  u16 frames  f32 frames per second
 *           frames x parts: f32 t[3]  i16 q[4]  i16 s[3] (/ 4096)
 * A vertex takes 8 bytes (12 with texture coordinates), a part in a frame 26. Everything is read at once; the meshes become ordinary meshes of the renderer. */
#ifndef FIRE_S3D_MODEL_H
#define FIRE_S3D_MODEL_H

#ifndef FIRE_S3D_ENGINE_H
#include "s3d_engine.h"
#endif

typedef struct S3dQuat { float x, y, z, w; } S3dQuat;
typedef struct S3dPose { S3dVec3 t; S3dQuat q; S3dVec3 s; } S3dPose;

typedef struct S3dModelPart {
    int parent;
    int hasColor;
    S3dVec3 color;
    S3dPose rest;
    S3dMesh* mesh;          /* NULL: a part without triangles (a joint) */
    char name[32];
} S3dModelPart;

typedef struct S3dModelClip {
    char name[32];
    int loop, frames;
    float fps;
    uint8_t* data;          /* frames x parts x 26 bytes, as in the file */
} S3dModelClip;

typedef struct S3dModel {
    int partN, clipN, refs;
    S3dModelPart* parts;
    S3dModelClip* clips;
    S3dVec3 bmin, bmax;
    s3d_size triangles;
} S3dModel;

static void s3d_modelFree(S3dModel* m) {
    int i;
    if (m == NULL) return;
    for (i = 0; i < m->partN; i++) if (m->parts[i].mesh) s3d_meshFree(m->parts[i].mesh);
    for (i = 0; i < m->clipN; i++) S3D_FREE(m->clips[i].data);
    S3D_FREE(m->parts); S3D_FREE(m->clips);
    S3D_FREE(m);
}

/* ---- reading ---- */
typedef struct S3dReader { const uint8_t* p; s3d_size n, at; int bad; } S3dReader;
static const uint8_t* s3d_take(S3dReader* r, s3d_size k) {
    const uint8_t* p;
    if (r->bad || r->at + k > r->n) { r->bad = 1; return NULL; }
    p = r->p + r->at; r->at += k;
    return p;
}
static uint32_t s3d_u8(S3dReader* r) { const uint8_t* p = s3d_take(r, 1); return p ? p[0] : 0; }
static uint32_t s3d_u16(S3dReader* r) { const uint8_t* p = s3d_take(r, 2); return p ? (uint32_t)p[0] | ((uint32_t)p[1] << 8) : 0; }
static int32_t s3d_i16(S3dReader* r) { uint32_t v = s3d_u16(r); return v >= 32768u ? (int32_t)v - 65536 : (int32_t)v; }
static uint32_t s3d_u32(S3dReader* r) { const uint8_t* p = s3d_take(r, 4); return p ? (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24) : 0; }
static float s3d_f32(S3dReader* r) { uint32_t u = s3d_u32(r); float f; memcpy(&f, &u, 4); return f; }
static void s3d_name(S3dReader* r, char* out) {
    uint32_t n = s3d_u8(r), i;
    const uint8_t* p = s3d_take(r, n);
    for (i = 0; i < n && i < 31; i++) out[i] = p ? (char)p[i] : '?';
    out[i < 31 ? i : 31] = 0;
}
static S3dQuat s3d_quatOf(int32_t x, int32_t y, int32_t z, int32_t w) {
    S3dQuat q;
    float l;
    q.x = (float)x / 32767.0f; q.y = (float)y / 32767.0f; q.z = (float)z / 32767.0f; q.w = (float)w / 32767.0f;
    l = sqrtf(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    if (l > 1e-12f) { q.x /= l; q.y /= l; q.z /= l; q.w /= l; } else { q.x = q.y = q.z = 0; q.w = 1; }
    return q;
}
/* The octahedral normal of two signed bytes. */
static S3dVec3 s3d_octNormal(int32_t a, int32_t b) {
    float x = (float)a / 127.0f, y = (float)b / 127.0f, z = 1.0f - fabsf(x) - fabsf(y);
    if (z < 0) {
        float ox = x;
        x = (1.0f - fabsf(y)) * (ox >= 0 ? 1.0f : -1.0f);
        y = (1.0f - fabsf(ox)) * (y >= 0 ? 1.0f : -1.0f);
    }
    return s3d_normalize(s3d_v3(x, y, z));
}

/* A model from the bytes of an .f3d file; NULL when they are not one (or the memory is gone). */
static S3dModel* s3d_modelRead(const uint8_t* data, s3d_size size) {
    S3dReader r;
    S3dModel* m;
    const uint8_t* magic;
    int i, k;
    r.p = data; r.n = size; r.at = 0; r.bad = 0;
    magic = s3d_take(&r, 4);
    if (magic == NULL || memcmp(magic, "F3D1", 4) != 0 || s3d_u16(&r) != 1) return NULL;
    m = (S3dModel*)s3d_calloc(1, sizeof(S3dModel));
    if (m == NULL) return NULL;
    m->refs = 1;
    m->partN = (int)s3d_u16(&r); m->clipN = (int)s3d_u16(&r); s3d_u16(&r);
    m->bmin.x = s3d_f32(&r); m->bmin.y = s3d_f32(&r); m->bmin.z = s3d_f32(&r);
    m->bmax.x = s3d_f32(&r); m->bmax.y = s3d_f32(&r); m->bmax.z = s3d_f32(&r);
    if (m->partN == 0 || (m->parts = (S3dModelPart*)s3d_calloc((s3d_size)m->partN, sizeof(S3dModelPart))) == NULL) { s3d_modelFree(m); return NULL; }
    if (m->clipN > 0 && (m->clips = (S3dModelClip*)s3d_calloc((s3d_size)m->clipN, sizeof(S3dModelClip))) == NULL) { s3d_modelFree(m); return NULL; }
    for (i = 0; i < m->partN && !r.bad; i++) {
        S3dModelPart* p = &m->parts[i];
        uint32_t flags, vn, in, v;
        int32_t qx, qy, qz, qw;
        S3dVec3 lo, size;
        p->parent = (int)s3d_i16(&r);
        flags = s3d_u8(&r);
        s3d_name(&r, p->name);
        if (p->parent >= i) { r.bad = 1; break; }   /* a parent comes first */
        p->rest.t.x = s3d_f32(&r); p->rest.t.y = s3d_f32(&r); p->rest.t.z = s3d_f32(&r);
        qx = s3d_i16(&r); qy = s3d_i16(&r); qz = s3d_i16(&r); qw = s3d_i16(&r);
        p->rest.q = s3d_quatOf(qx, qy, qz, qw);
        p->rest.s.x = s3d_f32(&r); p->rest.s.y = s3d_f32(&r); p->rest.s.z = s3d_f32(&r);
        p->color.x = (float)s3d_u8(&r) / 255.0f; p->color.y = (float)s3d_u8(&r) / 255.0f; p->color.z = (float)s3d_u8(&r) / 255.0f; s3d_u8(&r);
        p->hasColor = (flags & 1) != 0;
        vn = s3d_u16(&r); in = s3d_u32(&r);
        lo.x = s3d_f32(&r); lo.y = s3d_f32(&r); lo.z = s3d_f32(&r);
        size.x = s3d_f32(&r) - lo.x; size.y = s3d_f32(&r) - lo.y; size.z = s3d_f32(&r) - lo.z;
        if (vn == 0 || in < 3) { r.at += (s3d_size)vn * ((flags & 2) ? 12 : 8) + (s3d_size)in * 2; continue; }
        p->mesh = s3d_meshNew();
        if (p->mesh == NULL) { r.bad = 1; break; }
        if (!S3D_GROW(p->mesh->pos, p->mesh->posCap, (s3d_size)vn * 3) || !S3D_GROW(p->mesh->uv, p->mesh->uvCap, (s3d_size)vn * 2) ||
            !S3D_GROW(p->mesh->idx, p->mesh->idxCap, (s3d_size)in) || (p->mesh->nrm = (float*)S3D_MALLOC((s3d_size)vn * 3 * sizeof(float))) == NULL) { r.bad = 1; break; }
        for (v = 0; v < vn && !r.bad; v++) {
            int32_t x = s3d_i16(&r), y = s3d_i16(&r), z = s3d_i16(&r);
            const uint8_t* nb = s3d_take(&r, 2);
            S3dVec3 nrm = nb ? s3d_octNormal((int8_t)nb[0], (int8_t)nb[1]) : s3d_v3(0, 1, 0);
            float* pp = p->mesh->pos + (s3d_size)v * 3;
            pp[0] = lo.x + (float)(x + 32768) / 65535.0f * size.x;
            pp[1] = lo.y + (float)(y + 32768) / 65535.0f * size.y;
            pp[2] = lo.z + (float)(z + 32768) / 65535.0f * size.z;
            p->mesh->nrm[(s3d_size)v * 3] = nrm.x; p->mesh->nrm[(s3d_size)v * 3 + 1] = nrm.y; p->mesh->nrm[(s3d_size)v * 3 + 2] = nrm.z;
            if (flags & 2) { p->mesh->uv[(s3d_size)v * 2] = (float)s3d_i16(&r) / 1024.0f; p->mesh->uv[(s3d_size)v * 2 + 1] = (float)s3d_i16(&r) / 1024.0f; }
            else { p->mesh->uv[(s3d_size)v * 2] = 0; p->mesh->uv[(s3d_size)v * 2 + 1] = 0; }
        }
        p->mesh->posN = (s3d_size)vn * 3; p->mesh->uvN = (s3d_size)vn * 2;
        for (v = 0; v < in && !r.bad; v++) {
            uint32_t ix = s3d_u16(&r);
            if (ix >= vn) { r.bad = 1; break; }
            p->mesh->idx[v] = ix;
        }
        p->mesh->idxN = (s3d_size)(in / 3) * 3;
        p->mesh->nrmGiven = 1;
        if (r.bad || !s3d_meshFinish(p->mesh)) { r.bad = 1; break; }
        m->triangles += p->mesh->idxN / 3;
    }
    for (i = 0; i < m->clipN && !r.bad; i++) {
        S3dModelClip* c = &m->clips[i];
        s3d_size bytes;
        const uint8_t* src;
        s3d_name(&r, c->name);
        c->loop = (s3d_u8(&r) & 1) != 0;
        c->frames = (int)s3d_u16(&r);
        c->fps = s3d_f32(&r);
        if (c->frames < 1 || !(c->fps > 0)) { r.bad = 1; break; }
        bytes = (s3d_size)c->frames * (s3d_size)m->partN * 26;
        src = s3d_take(&r, bytes);
        if (src == NULL || (c->data = (uint8_t*)S3D_MALLOC(bytes)) == NULL) { r.bad = 1; break; }
        S3D_COPY(c->data, src, bytes);
    }
    for (k = 0; k < m->clipN; k++) if (m->clips[k].data == NULL) r.bad = 1;
    if (r.bad) { s3d_modelFree(m); return NULL; }
    return m;
}

/* ---- poses ---- */
static S3dPose s3d_framePose(const S3dModel* m, const S3dModelClip* c, int frame, int part) {
    S3dReader r;
    S3dPose p;
    int32_t qx, qy, qz, qw;
    r.p = c->data; r.n = (s3d_size)c->frames * (s3d_size)m->partN * 26; r.at = ((s3d_size)frame * (s3d_size)m->partN + (s3d_size)part) * 26; r.bad = 0;
    p.t.x = s3d_f32(&r); p.t.y = s3d_f32(&r); p.t.z = s3d_f32(&r);
    qx = s3d_i16(&r); qy = s3d_i16(&r); qz = s3d_i16(&r); qw = s3d_i16(&r);
    p.q = s3d_quatOf(qx, qy, qz, qw);
    p.s.x = (float)s3d_i16(&r) / 4096.0f; p.s.y = (float)s3d_i16(&r) / 4096.0f; p.s.z = (float)s3d_i16(&r) / 4096.0f;
    return p;
}
static S3dQuat s3d_nlerp(S3dQuat a, S3dQuat b, float t) {
    S3dQuat q;
    float d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w, s = d < 0 ? -1.0f : 1.0f, l;
    q.x = a.x + (b.x * s - a.x) * t; q.y = a.y + (b.y * s - a.y) * t; q.z = a.z + (b.z * s - a.z) * t; q.w = a.w + (b.w * s - a.w) * t;
    l = sqrtf(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    if (l > 1e-12f) { q.x /= l; q.y /= l; q.z /= l; q.w /= l; }
    return q;
}
/* The length of a clip in seconds (from the first frame to the last; a looping clip goes on from the last to the first). */
static float s3d_clipLength(const S3dModelClip* c) { return (float)(c->loop ? c->frames : c->frames - 1) / c->fps; }

/* The pose of a part at `time` seconds of a clip (clip -1: the rest pose); between two frames the places are interpolated. */
static S3dPose s3d_partPose(const S3dModel* m, int clip, float time, int part) {
    const S3dModelClip* c;
    float f, t;
    int a, b;
    S3dPose pa, pb, r;
    if (clip < 0 || clip >= m->clipN) return m->parts[part].rest;
    c = &m->clips[clip];
    if (c->frames == 1) return s3d_framePose(m, c, 0, part);
    f = time * c->fps;
    if (f < 0) f = 0;
    a = (int)f; t = f - (float)a;
    if (c->loop) { a %= c->frames; b = (a + 1) % c->frames; }
    else { if (a >= c->frames - 1) { a = c->frames - 1; t = 0; } b = a + 1 < c->frames ? a + 1 : a; }
    pa = s3d_framePose(m, c, a, part); pb = s3d_framePose(m, c, b, part);
    r.t = s3d_add(pa.t, s3d_scale(s3d_sub(pb.t, pa.t), t));
    r.s = s3d_add(pa.s, s3d_scale(s3d_sub(pb.s, pa.s), t));
    r.q = s3d_nlerp(pa.q, pb.q, t);
    return r;
}

/* The matrix of a pose: T * R(q) * S. */
static S3dMat4 s3d_poseMatrix(const S3dPose* p) {
    S3dMat4 r = s3d_identity();
    float x = p->q.x, y = p->q.y, z = p->q.z, w = p->q.w;
    S3D_AT(r, 0, 0) = (1 - 2 * (y * y + z * z)) * p->s.x; S3D_AT(r, 0, 1) = (2 * (x * y - z * w)) * p->s.y; S3D_AT(r, 0, 2) = (2 * (x * z + y * w)) * p->s.z;
    S3D_AT(r, 1, 0) = (2 * (x * y + z * w)) * p->s.x; S3D_AT(r, 1, 1) = (1 - 2 * (x * x + z * z)) * p->s.y; S3D_AT(r, 1, 2) = (2 * (y * z - x * w)) * p->s.z;
    S3D_AT(r, 2, 0) = (2 * (x * z - y * w)) * p->s.x; S3D_AT(r, 2, 1) = (2 * (y * z + x * w)) * p->s.y; S3D_AT(r, 2, 2) = (1 - 2 * (x * x + y * y)) * p->s.z;
    S3D_AT(r, 0, 3) = p->t.x; S3D_AT(r, 1, 3) = p->t.y; S3D_AT(r, 2, 3) = p->t.z;
    return r;
}

/* The inverse transpose of the 3x3 part of a matrix (the matrix of its normals; the renderers normalise them). */
static S3dMat4 s3d_normalMatrix(const S3dMat4* m) {
    float a = S3D_AT(*m, 0, 0), b = S3D_AT(*m, 0, 1), c = S3D_AT(*m, 0, 2), d = S3D_AT(*m, 1, 0), e = S3D_AT(*m, 1, 1), f = S3D_AT(*m, 1, 2);
    float g = S3D_AT(*m, 2, 0), h = S3D_AT(*m, 2, 1), i = S3D_AT(*m, 2, 2);
    float A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g, det = a * A + b * B + c * C, k;
    S3dMat4 r = s3d_identity();
    if (det == 0) return r;
    k = 1.0f / det;
    /* (inverse)^T = cofactors / det */
    S3D_AT(r, 0, 0) = A * k; S3D_AT(r, 0, 1) = B * k; S3D_AT(r, 0, 2) = C * k;
    S3D_AT(r, 1, 0) = -(b * i - c * h) * k; S3D_AT(r, 1, 1) = (a * i - c * g) * k; S3D_AT(r, 1, 2) = -(a * h - b * g) * k;
    S3D_AT(r, 2, 0) = (b * f - c * e) * k; S3D_AT(r, 2, 1) = -(a * f - c * d) * k; S3D_AT(r, 2, 2) = (a * e - b * d) * k;
    return r;
}

/* The matrices of all parts in the model's space at a time of a clip (`out`: partN matrices). */
static void s3d_modelPose(const S3dModel* m, int clip, float time, S3dMat4* out) {
    int i;
    for (i = 0; i < m->partN; i++) {
        S3dPose p = s3d_partPose(m, clip, time, i);
        S3dMat4 local = s3d_poseMatrix(&p);
        out[i] = m->parts[i].parent >= 0 ? s3d_mul(out[m->parts[i].parent], local) : local;
    }
}

static int s3d_modelClipIndex(const S3dModel* m, const char* name) {
    int i;
    for (i = 0; i < m->clipN; i++) if (strcmp(m->clips[i].name, name) == 0) return i;
    return -1;
}

#endif /* FIRE_S3D_MODEL_H */
