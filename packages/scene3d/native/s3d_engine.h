/* fire scene3d: the renderer behind `#import "scene3d"` (docs/graphics/3d.md). Plain C99 that knows nothing of fire: triangles go in, RGBA pixels come out.
 *
 * What it does: a perspective camera (vertical field of view), the model matrix of an object from its position, rotation, scale and pivot, clipping against the near plane, back-face
 * culling, a depth buffer (1/w interpolation of z in screen space), perspective-correct interpolation of the texture coordinates, the normal and the world position, textures (repeat,
 * bilinear), and Blinn-Phong lighting with an ambient, directional and point lights per pixel. A mesh with smooth normals is shaded with the interpolated vertex normal, a flat one with the
 * normal of its face.
 *
 * Drawing is queue-then-flush: `s3d_queueMesh` records an object (its matrices, its surface, its mesh and texture), `s3d_flush` draws the queue. The software renderer (this file) transforms
 * the objects and sets up their triangles, then rasterises them (opaque objects first, then the transparent ones from far to near) in bands of rows that are shared between threads. Also:
 * transparency (opacity, textures with alpha, cut-outs), billboards, shadow maps (directional and point lights, soft edge), bump mapping, lights and surfaces that can be switched off or
 * limited to layers.
 *
 * The same queue can be drawn by the hardware backend instead (s3d_gpu_vk.h: Vulkan, loaded at run time; FlushSettings.backend). It is optional: where there is no GPU, no driver or no
 * Vulkan, the software renderer draws, and the software renderer is what gives the same pixels everywhere (the VM, a native build, an ESP32, the browser).
 *
 * One implementation for everything: the native build puts it into the program and the virtual machine runs it in a shared library (native/abi/fire_pkg_abi.h).
 * Right handed, Y up; a camera without rotation looks along -Z; angles in radians, lengths in meters (the fire side converts the units).
 *
 * (A port of the C++ renderer: every computation is done in the same order with the same float functions, so the pixels are the same as before.) */
#ifndef FIRE_S3D_ENGINE_H
#define FIRE_S3D_ENGINE_H

#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The memory of the engine: its buffers (depth, shadow maps, textures, meshes) can be larger than a size_t of 16 bits holds (win16, dos16 in the huge
 * model). s3d_size counts them (32 bits there); S3D_MALLOC/S3D_FREE/S3D_COPY/S3D_ZERO get and fill them - the C library unless the includer gives others
 * (fire's bridge gives the allocator of its runtime: dataAlloc, which takes such blocks from the system). */
#if defined(__WATCOMC__) && defined(_M_I86)
typedef uint32_t s3d_size;
#else
typedef size_t s3d_size;
#endif
#if !defined(S3D_MALLOC) && defined(FIRE_RT_H)
/* (inside a fire program or package library: fire's runtime, whose dataAlloc takes a block beyond 64 KB from the system - elsewhere it is malloc) */
#define S3D_MALLOC(bytes) dataAlloc(0, (uint32_t)(bytes), 1)
#define S3D_FREE(p) dataFree(p)
#define S3D_COPY(to, from, bytes) dataCopy((to), (from), (uint32_t)(bytes))
#define S3D_ZERO(p, bytes) dataZero((p), (uint32_t)(bytes))
#endif
#ifndef S3D_MALLOC
#define S3D_MALLOC(bytes) malloc((size_t)(bytes))
#define S3D_FREE(p) free(p)
#define S3D_COPY(to, from, bytes) memcpy((to), (from), (size_t)(bytes))
#define S3D_ZERO(p, bytes) memset((p), 0, (size_t)(bytes))
#endif
static void* s3d_calloc(s3d_size count, s3d_size elem) {
    s3d_size bytes = count * elem;
    void* p = S3D_MALLOC(bytes ? bytes : 1);
    if (p != NULL) S3D_ZERO(p, bytes);
    return p;
}

#if !defined(FIRE_S3D_NO_THREADS) && !defined(__EMSCRIPTEN__) && !defined(ESP_PLATFORM) && (defined(_WIN32) || defined(__unix__) || defined(__APPLE__))
#define S3D_THREADS 1
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <pthread.h>
#include <unistd.h>
#endif
#endif

/* ---- vectors and matrices ---------------------------------------------------------------------------------------------------------------- */
typedef struct S3dVec3 { float x, y, z; } S3dVec3;
static inline S3dVec3 s3d_v3(float x, float y, float z) { S3dVec3 r; r.x = x; r.y = y; r.z = z; return r; }
static inline S3dVec3 s3d_add(S3dVec3 a, S3dVec3 b) { return s3d_v3(a.x + b.x, a.y + b.y, a.z + b.z); }
static inline S3dVec3 s3d_sub(S3dVec3 a, S3dVec3 b) { return s3d_v3(a.x - b.x, a.y - b.y, a.z - b.z); }
static inline S3dVec3 s3d_scale(S3dVec3 a, float s) { return s3d_v3(a.x * s, a.y * s, a.z * s); }
static inline float s3d_dot(S3dVec3 a, S3dVec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static inline S3dVec3 s3d_cross(S3dVec3 a, S3dVec3 b) { return s3d_v3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
static inline float s3d_length(S3dVec3 a) { return sqrtf(s3d_dot(a, a)); }
static inline S3dVec3 s3d_normalize(S3dVec3 a) { float l = s3d_length(a); return l > 1e-20f ? s3d_scale(a, 1.0f / l) : s3d_v3(0, 1, 0); }

/* min and max as std::min / std::max (the first one when they are equal): the float ones stay plain comparisons (fminf would be slower and differ for NaN) */
static inline float s3d_minf(float a, float b) { return b < a ? b : a; }
static inline float s3d_maxf(float a, float b) { return a < b ? b : a; }
static inline int s3d_mini(int a, int b) { return b < a ? b : a; }
static inline int s3d_maxi(int a, int b) { return a < b ? b : a; }
static inline float s3d_min3f(float a, float b, float c) { return s3d_minf(s3d_minf(a, b), c); }
static inline float s3d_max3f(float a, float b, float c) { return s3d_maxf(s3d_maxf(a, b), c); }

/* A 4x4 matrix, row major, applied to column vectors: v' = M * v. */
typedef struct S3dMat4 { float m[16]; } S3dMat4;
#define S3D_AT(M, row, col) ((M).m[(row) * 4 + (col)])
static inline S3dMat4 s3d_identity(void) { S3dMat4 r; int i; for (i = 0; i < 16; i++) r.m[i] = (i % 5 == 0) ? 1.0f : 0.0f; return r; }
static inline S3dMat4 s3d_mul(S3dMat4 a, S3dMat4 b) {
    S3dMat4 r;
    int i, j, k;
    for (i = 0; i < 4; i++)
        for (j = 0; j < 4; j++) {
            float s = 0;
            for (k = 0; k < 4; k++) s += S3D_AT(a, i, k) * S3D_AT(b, k, j);
            S3D_AT(r, i, j) = s;
        }
    return r;
}
static inline S3dMat4 s3d_translation(S3dVec3 t) { S3dMat4 r = s3d_identity(); S3D_AT(r, 0, 3) = t.x; S3D_AT(r, 1, 3) = t.y; S3D_AT(r, 2, 3) = t.z; return r; }
static inline S3dMat4 s3d_scaling(S3dVec3 s) { S3dMat4 r = s3d_identity(); S3D_AT(r, 0, 0) = s.x; S3D_AT(r, 1, 1) = s.y; S3D_AT(r, 2, 2) = s.z; return r; }
static inline S3dMat4 s3d_rotationX(float a) { S3dMat4 r = s3d_identity(); float c = cosf(a), s = sinf(a); S3D_AT(r, 1, 1) = c; S3D_AT(r, 1, 2) = -s; S3D_AT(r, 2, 1) = s; S3D_AT(r, 2, 2) = c; return r; }
static inline S3dMat4 s3d_rotationY(float a) { S3dMat4 r = s3d_identity(); float c = cosf(a), s = sinf(a); S3D_AT(r, 0, 0) = c; S3D_AT(r, 0, 2) = s; S3D_AT(r, 2, 0) = -s; S3D_AT(r, 2, 2) = c; return r; }
static inline S3dMat4 s3d_rotationZ(float a) { S3dMat4 r = s3d_identity(); float c = cosf(a), s = sinf(a); S3D_AT(r, 0, 0) = c; S3D_AT(r, 0, 1) = -s; S3D_AT(r, 1, 0) = s; S3D_AT(r, 1, 1) = c; return r; }
/* The rotation of an object: first around Z (roll), then X (pitch), then Y (yaw). */
static inline S3dMat4 s3d_rotationXYZ(float rx, float ry, float rz) { return s3d_mul(s3d_rotationY(ry), s3d_mul(s3d_rotationX(rx), s3d_rotationZ(rz))); }
static inline S3dVec3 s3d_transformPoint(const S3dMat4* m, S3dVec3 p) {
    return s3d_v3(S3D_AT(*m, 0, 0) * p.x + S3D_AT(*m, 0, 1) * p.y + S3D_AT(*m, 0, 2) * p.z + S3D_AT(*m, 0, 3),
                  S3D_AT(*m, 1, 0) * p.x + S3D_AT(*m, 1, 1) * p.y + S3D_AT(*m, 1, 2) * p.z + S3D_AT(*m, 1, 3),
                  S3D_AT(*m, 2, 0) * p.x + S3D_AT(*m, 2, 1) * p.y + S3D_AT(*m, 2, 2) * p.z + S3D_AT(*m, 2, 3));
}
static inline S3dVec3 s3d_transformDir(const S3dMat4* m, S3dVec3 p) {
    return s3d_v3(S3D_AT(*m, 0, 0) * p.x + S3D_AT(*m, 0, 1) * p.y + S3D_AT(*m, 0, 2) * p.z,
                  S3D_AT(*m, 1, 0) * p.x + S3D_AT(*m, 1, 1) * p.y + S3D_AT(*m, 1, 2) * p.z,
                  S3D_AT(*m, 2, 0) * p.x + S3D_AT(*m, 2, 1) * p.y + S3D_AT(*m, 2, 2) * p.z);
}

/* Makes room for `need` elements of `elem` bytes in a growing array: 1, or 0 when the memory is gone (the array is unchanged then). */
static int s3d_grow(void** data, s3d_size* cap, s3d_size need, s3d_size elem) {
    s3d_size c;
    void* p;
    if (need <= *cap) return 1;
    c = *cap ? *cap : 16;
    while (c < need) c *= 2;
    p = S3D_MALLOC(c * elem);
    if (p == NULL) return 0;
    if (*data != NULL) { S3D_COPY(p, *data, *cap * elem); S3D_FREE(*data); }
    *data = p;
    *cap = c;
    return 1;
}
#define S3D_GROW(ptr, cap, need) s3d_grow((void**)&(ptr), &(cap), (need), sizeof(*(ptr)))

/* ---- meshes and textures ----------------------------------------------------------------------------------------------------------------- */
/* A triangle mesh: positions, texture coordinates, and the vertex normals (computed here as the area weighted average of the faces around a vertex). A mesh does not change after
 * s3d_meshFinish (the hardware backend keeps a copy of it on the GPU).
 * It is built with floats (s3d_addVertex, s3d_addTriangle, or the arrays directly); s3d_meshFinish makes it COMPACT and lets the floats go: positions as 16-bit numbers in the
 * box of the mesh, normals in two bytes (octahedral), texture coordinates as 16-bit numbers (none when they are all 0), indices of 16 bits when there are fewer than 65536
 * corners - 8 to 12 bytes a corner and 2 an index instead of 32 and 4 (ROADMAP 16.1 A). The renderers read a finished mesh through s3d_meshPos, s3d_meshNrm, s3d_meshUv and
 * s3d_meshIdx. (Where the memory for the compact form is missing, the floats stay; S3D_MESH_FLOAT keeps them always.) */
typedef struct S3dMesh {
    float* pos; s3d_size posN, posCap;      /* 3 per vertex */
    float* uv; s3d_size uvN, uvCap;         /* 2 per vertex */
    float* nrm;                           /* 3 per vertex */
    uint32_t* idx; s3d_size idxN, idxCap;   /* 3 per triangle */
    S3dVec3 bmin, bmax;
    int refs;                             /* the users of the mesh (see MeshFree) */
    int bad;                              /* the memory ran out while it was made */
    int nrmGiven;                         /* the normals came with the mesh (a model file): s3d_meshFinish keeps them */
    void* gpu;                            /* the copy of the hardware backend, or NULL */
    /* the compact form (compact = 1 after s3d_meshFinish) */
    int compact;
    int16_t* qpos;                        /* 3 per vertex: bmin + (q + 32768) * qstep */
    int8_t* qnrm;                         /* 2 per vertex: octahedral, / 127 */
    int16_t* quv;                         /* 2 per vertex: q * uvStep (NULL: all 0) */
    uint16_t* idx16;                      /* the indices, when they fit 16 bits (else `idx` stays) */
    float qstep[3], uvStep;
    int uvKnown;                          /* uvLo/uvHi hold the bounds of the texture coordinates (s3d_meshUvBounds; a finished mesh does not change) */
    float uvLo[2], uvHi[2];
} S3dMesh;

typedef struct S3dTexture S3dTexture;
/* the hardware backend lets go of its copies (s3d_gpu_vk.h) */
static void s3d_gpu_releaseMesh(S3dMesh* m);
static void s3d_gpu_releaseTexture(S3dTexture* t);

static S3dMesh* s3d_meshNew(void) {
    S3dMesh* m = (S3dMesh*)s3d_calloc(1, sizeof(S3dMesh));
    if (m) m->refs = 1;
    return m;
}
static void s3d_meshFree(S3dMesh* m) {
    if (m == NULL) return;
    s3d_gpu_releaseMesh(m);
    S3D_FREE(m->pos); S3D_FREE(m->uv); S3D_FREE(m->nrm); S3D_FREE(m->idx);
    S3D_FREE(m->qpos); S3D_FREE(m->qnrm); S3D_FREE(m->quv); S3D_FREE(m->idx16);
    S3D_FREE(m);
}
static void s3d_addVertex(S3dMesh* m, float x, float y, float z, float u, float v) {
    if (!S3D_GROW(m->pos, m->posCap, m->posN + 3) || !S3D_GROW(m->uv, m->uvCap, m->uvN + 2)) { m->bad = 1; return; }
    m->pos[m->posN++] = x; m->pos[m->posN++] = y; m->pos[m->posN++] = z;
    m->uv[m->uvN++] = u; m->uv[m->uvN++] = v;
}
static void s3d_addTriangle(S3dMesh* m, uint32_t a, uint32_t b, uint32_t c) {
    if (!S3D_GROW(m->idx, m->idxCap, m->idxN + 3)) { m->bad = 1; return; }
    m->idx[m->idxN++] = a; m->idx[m->idxN++] = b; m->idx[m->idxN++] = c;
}

/* ---- the compact form ---- */
static inline S3dVec3 s3d_meshPos(const S3dMesh* m, s3d_size i) {
    if (m->compact) {
        const int16_t* q = m->qpos + i * 3;
        return s3d_v3(m->bmin.x + (float)((int32_t)q[0] + 32768) * m->qstep[0], m->bmin.y + (float)((int32_t)q[1] + 32768) * m->qstep[1],
                      m->bmin.z + (float)((int32_t)q[2] + 32768) * m->qstep[2]);
    }
    return s3d_v3(m->pos[i * 3], m->pos[i * 3 + 1], m->pos[i * 3 + 2]);
}
/* The normal (unit length) of an octahedral pair. */
static inline S3dVec3 s3d_octDecode(int32_t a, int32_t b) {
    float x = (float)a / 127.0f, y = (float)b / 127.0f, z = 1.0f - fabsf(x) - fabsf(y);
    if (z < 0) {
        float ox = x;
        x = (1.0f - fabsf(y)) * (ox >= 0 ? 1.0f : -1.0f);
        y = (1.0f - fabsf(ox)) * (y >= 0 ? 1.0f : -1.0f);
    }
    return s3d_normalize(s3d_v3(x, y, z));
}
static inline void s3d_octEncode(S3dVec3 n, int8_t* out) {
    float l = fabsf(n.x) + fabsf(n.y) + fabsf(n.z), x, y;
    if (l < 1e-20f) { out[0] = 0; out[1] = 127; return; }
    x = n.x / l; y = n.y / l;
    if (n.z < 0) { float ox = x; x = (1.0f - fabsf(y)) * (ox >= 0 ? 1.0f : -1.0f); y = (1.0f - fabsf(ox)) * (y >= 0 ? 1.0f : -1.0f); }
    out[0] = (int8_t)(x * 127.0f + (x >= 0 ? 0.5f : -0.5f));
    out[1] = (int8_t)(y * 127.0f + (y >= 0 ? 0.5f : -0.5f));
}
static inline S3dVec3 s3d_meshNrm(const S3dMesh* m, s3d_size i) {
    if (m->compact) return s3d_octDecode(m->qnrm[i * 2], m->qnrm[i * 2 + 1]);
    return s3d_v3(m->nrm[i * 3], m->nrm[i * 3 + 1], m->nrm[i * 3 + 2]);
}
static inline void s3d_meshUv(const S3dMesh* m, s3d_size i, float* u, float* v) {
    if (m->compact) {
        if (m->quv == NULL) { *u = 0; *v = 0; return; }
        *u = (float)m->quv[i * 2] * m->uvStep; *v = (float)m->quv[i * 2 + 1] * m->uvStep;
        return;
    }
    *u = m->uv[i * 2]; *v = m->uv[i * 2 + 1];
}
static inline uint32_t s3d_meshIdx(const S3dMesh* m, s3d_size k) { return m->idx16 ? (uint32_t)m->idx16[k] : m->idx[k]; }

/* Makes a finished mesh compact (its bounds and normals are there) and lets the floats go; without the memory it stays as it is. */
static void s3d_meshCompact(S3dMesh* m) {
#ifdef S3D_MESH_FLOAT
    (void)m;
#else
    s3d_size n = m->posN / 3, i, k;
    float ext[3], uvMax = 0;
    int16_t* qpos;
    int8_t* qnrm;
    int16_t* quv = NULL;
    uint16_t* idx16 = NULL;
    if (m->compact || n == 0) return;
    ext[0] = m->bmax.x - m->bmin.x; ext[1] = m->bmax.y - m->bmin.y; ext[2] = m->bmax.z - m->bmin.z;
    for (i = 0; i < n * 2; i++) { float a = fabsf(m->uv[i]); if (a > uvMax) uvMax = a; }
    qpos = (int16_t*)S3D_MALLOC(n * 3 * sizeof(int16_t));
    qnrm = (int8_t*)S3D_MALLOC(n * 2);
    if (uvMax > 0) quv = (int16_t*)S3D_MALLOC(n * 2 * sizeof(int16_t));
    if (n < 65536) idx16 = (uint16_t*)S3D_MALLOC(m->idxN * sizeof(uint16_t) + 2);
    if (qpos == NULL || qnrm == NULL || (uvMax > 0 && quv == NULL) || (n < 65536 && idx16 == NULL)) { S3D_FREE(qpos); S3D_FREE(qnrm); S3D_FREE(quv); S3D_FREE(idx16); return; }
    for (k = 0; k < 3; k++) m->qstep[k] = ext[k] > 0 ? ext[k] / 65535.0f : 0.0f;
    for (i = 0; i < n; i++) {
        const float* p = m->pos + i * 3;
        float lo[3];
        lo[0] = m->bmin.x; lo[1] = m->bmin.y; lo[2] = m->bmin.z;
        for (k = 0; k < 3; k++) {
            float q = ext[k] > 0 ? (p[k] - lo[k]) / ext[k] * 65535.0f + 0.5f : 0.0f;
            int32_t v = (int32_t)q;
            qpos[i * 3 + k] = (int16_t)((v < 0 ? 0 : v > 65535 ? 65535 : v) - 32768);
        }
        s3d_octEncode(s3d_v3(m->nrm[i * 3], m->nrm[i * 3 + 1], m->nrm[i * 3 + 2]), qnrm + i * 2);
    }
    if (quv) {
        m->uvStep = uvMax <= 32.0f ? 1.0f / 1024.0f : uvMax / 32767.0f;
        for (i = 0; i < n * 2; i++) { float q = m->uv[i] / m->uvStep; quv[i] = (int16_t)(q >= 0 ? q + 0.5f : q - 0.5f); }
    }
    if (idx16) { for (i = 0; i < m->idxN; i++) idx16[i] = (uint16_t)m->idx[i]; S3D_FREE(m->idx); m->idx = NULL; m->idxCap = 0; }
    S3D_FREE(m->pos); S3D_FREE(m->uv); S3D_FREE(m->nrm);
    m->pos = NULL; m->uv = NULL; m->nrm = NULL; m->posCap = m->uvCap = 0;
    m->qpos = qpos; m->qnrm = qnrm; m->quv = quv; m->idx16 = idx16;
    m->compact = 1;
#endif
}

/* The bytes a mesh takes (its arrays; for the measurements). */
static s3d_size s3d_meshBytes(const S3dMesh* m) {
    s3d_size n = m->posN / 3;
    if (m->compact) return n * 3 * 2 + n * 2 + (m->quv ? n * 4 : 0) + m->idxN * (m->idx16 ? 2 : 4);
    return n * 32 + m->idxN * 4;
}

/* The bounds and the vertex normals; the texture coordinates get one pair per vertex; then the mesh is made compact. 0 when the memory is gone. */
static int s3d_meshFinishFloats(S3dMesh* m);
static int s3d_meshFinish(S3dMesh* m) {
    if (!s3d_meshFinishFloats(m)) return 0;
    s3d_meshCompact(m);
    return 1;
}
static int s3d_meshFinishFloats(S3dMesh* m) {
    s3d_size n = m->posN / 3, i, t;
    if (m->bad) return 0;
    if (!S3D_GROW(m->uv, m->uvCap, n * 2)) return 0;
    for (i = m->uvN; i < n * 2; i++) m->uv[i] = 0.0f;
    m->uvN = n * 2;
    if (!m->nrmGiven || m->nrm == NULL) {
        S3D_FREE(m->nrm);
        m->nrm = (float*)s3d_calloc(n * 3 + 1, sizeof(float));
        if (m->nrm == NULL) return 0;
        m->nrmGiven = 0;
    }
    if (n == 0) { m->bmin = m->bmax = s3d_v3(0, 0, 0); return 1; }
    m->bmin = m->bmax = s3d_v3(m->pos[0], m->pos[1], m->pos[2]);
    for (i = 0; i < n; i++) {
        const float* p = m->pos + i * 3;
        m->bmin = s3d_v3(s3d_minf(m->bmin.x, p[0]), s3d_minf(m->bmin.y, p[1]), s3d_minf(m->bmin.z, p[2]));
        m->bmax = s3d_v3(s3d_maxf(m->bmax.x, p[0]), s3d_maxf(m->bmax.y, p[1]), s3d_maxf(m->bmax.z, p[2]));
    }
    if (m->nrmGiven) return 1;
    for (t = 0; t + 2 < m->idxN; t += 3) {
        uint32_t v[3];
        S3dVec3 pa, pb, pc, fn;
        int k;
        v[0] = m->idx[t]; v[1] = m->idx[t + 1]; v[2] = m->idx[t + 2];
        if (v[0] >= n || v[1] >= n || v[2] >= n) continue;
        pa = s3d_v3(m->pos[v[0] * 3], m->pos[v[0] * 3 + 1], m->pos[v[0] * 3 + 2]);
        pb = s3d_v3(m->pos[v[1] * 3], m->pos[v[1] * 3 + 1], m->pos[v[1] * 3 + 2]);
        pc = s3d_v3(m->pos[v[2] * 3], m->pos[v[2] * 3 + 1], m->pos[v[2] * 3 + 2]);
        fn = s3d_cross(s3d_sub(pb, pa), s3d_sub(pc, pa));   /* its length is twice the area: the weight */
        for (k = 0; k < 3; k++) { m->nrm[v[k] * 3] += fn.x; m->nrm[v[k] * 3 + 1] += fn.y; m->nrm[v[k] * 3 + 2] += fn.z; }
    }
    for (i = 0; i < n; i++) {
        S3dVec3 v = s3d_normalize(s3d_v3(m->nrm[i * 3], m->nrm[i * 3 + 1], m->nrm[i * 3 + 2]));
        m->nrm[i * 3] = v.x; m->nrm[i * 3 + 1] = v.y; m->nrm[i * 3 + 2] = v.z;
    }
    return 1;
}

struct S3dTexture {
    int w, h;
    uint8_t* rgba;
    uint8_t* bump;     /* a height (0..255) per texel, for bump mapping; NULL: none */
    int hasAlpha;      /* some texel is not opaque (see s3d_texPrepare) */
    uint32_t version;  /* counts the changes (the bump map): the hardware backend loads the texture again */
    void* gpu;         /* the copy of the hardware backend, or NULL */
    int refs;          /* (the bridge: the users of the texture - its fire object and the objects that show it) */
    uint8_t* lean;     /* the lean renderer (s3d_lean.h): the texels as indices of the palette `leanKey`, or NULL */
    uint32_t leanKey;
    float strength;    /* (the bridge: how strongly the bump map tilts the light, Texture.BumpStrength) */
    int filter;        /* how the colours are read between the texels: S3D_FILTER_* (the bump map is always bilinear; the lean renderer always reads the nearest texel) */
    /* where the texels that are not opaque are (made when asked, see s3d_texReadsAlpha): the texture in cells of alphaCell x alphaCell texels (at most 256 across), as sums over the
     * rectangles from the top left corner of the cells that hold such a texel ((alphaGw + 1) x (alphaGh + 1)); alphaVersion: the version they were made for */
    uint32_t* alphaSum;
    int alphaCell, alphaGw, alphaGh;
    uint32_t alphaVersion;
};

/* Texture.Filter: bilinear (smooth), the nearest texel (hard pixels), or sharp bilinear (hard pixels, but a one screen pixel wide blend at the texel edges: no shimmer when the texels are not a whole number of
 * pixels wide). */
enum { S3D_FILTER_BILINEAR = 0, S3D_FILTER_NEAREST = 1, S3D_FILTER_SHARP = 2 };

static void s3d_texFree(S3dTexture* t) {
    if (t == NULL) return;
    s3d_gpu_releaseTexture(t);
    S3D_FREE(t->rgba); S3D_FREE(t->bump); S3D_FREE(t->lean); S3D_FREE(t->alphaSum);
    S3D_FREE(t);
}
static void s3d_texPrepare(S3dTexture* t) {
    s3d_size i, n = (s3d_size)t->w * (s3d_size)t->h * 4;
    t->hasAlpha = 0;
    for (i = 3; i < n; i += 4) if (t->rgba[i] != 255) { t->hasAlpha = 1; break; }
    t->version++;
}

/* The cells of the texture that hold a texel that is not opaque, as sums (see S3dTexture.alphaSum). 0 when the memory is gone. */
static int s3d_texAlphaCells(S3dTexture* t) {
    int cell = 1, gw, gh, x, y;
    uint32_t* sum;
    if (t->alphaSum != NULL && t->alphaVersion == t->version) return 1;
    while ((t->w + cell - 1) / cell > 256 || (t->h + cell - 1) / cell > 256) cell *= 2;
    gw = (t->w + cell - 1) / cell; gh = (t->h + cell - 1) / cell;
    S3D_FREE(t->alphaSum);
    t->alphaSum = sum = (uint32_t*)S3D_MALLOC((s3d_size)(gw + 1) * (s3d_size)(gh + 1) * sizeof(uint32_t));
    if (sum == NULL) return 0;
    S3D_ZERO(sum, (s3d_size)(gw + 1) * (s3d_size)(gh + 1) * sizeof(uint32_t));
    for (y = 0; y < t->h; y++)
        for (x = 0; x < t->w; x++)
            if (t->rgba[((s3d_size)y * (s3d_size)t->w + (s3d_size)x) * 4 + 3] != 255) sum[(s3d_size)(y / cell + 1) * (s3d_size)(gw + 1) + (s3d_size)(x / cell + 1)] = 1;
    for (y = 1; y <= gh; y++)
        for (x = 1; x <= gw; x++)
            sum[(s3d_size)y * (s3d_size)(gw + 1) + x] += sum[(s3d_size)(y - 1) * (s3d_size)(gw + 1) + x] + sum[(s3d_size)y * (s3d_size)(gw + 1) + x - 1] - sum[(s3d_size)(y - 1) * (s3d_size)(gw + 1) + x - 1];
    t->alphaCell = cell; t->alphaGw = gw; t->alphaGh = gh; t->alphaVersion = t->version;
    return 1;
}
/* The ranges of texels x0..x1 (any whole numbers, repeat mode) as at most two ranges of cells inside the texture; the count. */
static int s3d_texCellRanges(int x0, int x1, int size, int cell, int* lo, int* hi) {
    int a, b;
    if (x1 - x0 + 1 >= size) { lo[0] = 0; hi[0] = (size - 1) / cell; return 1; }
    a = ((x0 % size) + size) % size; b = a + (x1 - x0);
    if (b < size) { lo[0] = a / cell; hi[0] = b / cell; return 1; }
    lo[0] = a / cell; hi[0] = (size - 1) / cell; lo[1] = 0; hi[1] = (b - size) / cell;
    return 2;
}
/* Whether a mesh whose texture coordinates lie in u0..u1, v0..v1 (repeat mode) can read a texel that is not opaque (with the texels next to them that the filter mixes in).
 * 1 when it can or when that is not known (the memory). */
static int s3d_texReadsAlpha(S3dTexture* t, float u0, float u1, float v0, float v1) {
    int xl[2], xh[2], yl[2], yh[2], nx, ny, i, k;
    double fu, fv;
    if (!t->hasAlpha) return 0;
    if (!(u1 - u0 < 1e6f) || !(v1 - v0 < 1e6f) || !(fabsf(u0) < 1e9f) || !(fabsf(v0) < 1e9f)) return 1;   /* (not a number, or too far) */
    if (!s3d_texAlphaCells(t)) return 1;
    /* (the hardware renderer reads the nearest texel clamped to the edge: outside 0..1 that is not the texel of repeat mode - then all of them count) */
    if (t->filter == S3D_FILTER_NEAREST && (u0 < 0 || v0 < 0 || u1 > 1 || v1 > 1)) { u0 = v0 = 0; u1 = v1 = 2; }
    fu = floor((double)u0); fv = floor((double)v0);
    {
        double x0 = ((double)u0 - fu) * t->w - 0.5, x1 = ((double)u1 - fu) * t->w - 0.5, y0 = ((double)v0 - fv) * t->h - 0.5, y1 = ((double)v1 - fv) * t->h - 0.5;
        if (x1 - x0 > 2.0 * t->w) x1 = x0 + 2.0 * t->w;
        if (y1 - y0 > 2.0 * t->h) y1 = y0 + 2.0 * t->h;
        /* (a texel more on each side: rounding, the sharp filter) */
        nx = s3d_texCellRanges((int)floor(x0) - 1, (int)floor(x1) + 2, t->w, t->alphaCell, xl, xh);
        ny = s3d_texCellRanges((int)floor(y0) - 1, (int)floor(y1) + 2, t->h, t->alphaCell, yl, yh);
    }
    for (i = 0; i < nx; i++)
        for (k = 0; k < ny; k++) {
            s3d_size W = (s3d_size)t->alphaGw + 1;
            const uint32_t* s = t->alphaSum;
            uint32_t n = s[(s3d_size)(yh[k] + 1) * W + (s3d_size)(xh[i] + 1)] - s[(s3d_size)yl[k] * W + (s3d_size)(xh[i] + 1)] - s[(s3d_size)(yh[k] + 1) * W + (s3d_size)xl[i]] + s[(s3d_size)yl[k] * W + (s3d_size)xl[i]];
            if (n) return 1;
        }
    return 0;
}

/* The heights from the brightness of the texture itself (a little smoothed: a sharp edge gives a bevel, not a one texel crack). 0 when the memory is gone. */
static int s3d_texBumpFromColors(S3dTexture* t) {
    s3d_size n = (s3d_size)t->w * (s3d_size)t->h, i;
    int x, y, dx, dy, w = t->w, h = t->h;
    float* lum = (float*)S3D_MALLOC(n * sizeof(float) + 1);
    uint8_t* bump = (uint8_t*)S3D_MALLOC(n + 1);
    if (lum == NULL || bump == NULL) { S3D_FREE(lum); S3D_FREE(bump); return 0; }
    for (i = 0; i < n; i++) lum[i] = 0.299f * t->rgba[i * 4] + 0.587f * t->rgba[i * 4 + 1] + 0.114f * t->rgba[i * 4 + 2];
    for (y = 0; y < h; y++)
        for (x = 0; x < w; x++) {
            float sum = 0;
            for (dy = -1; dy <= 1; dy++)
                for (dx = -1; dx <= 1; dx++) sum += lum[(s3d_size)((y + dy + h) % h) * (s3d_size)w + (s3d_size)((x + dx + w) % w)] * ((dx == 0 && dy == 0) ? 4.0f : (dx == 0 || dy == 0) ? 2.0f : 1.0f);
            bump[(s3d_size)y * (s3d_size)w + (s3d_size)x] = (uint8_t)s3d_minf(255.0f, sum / 16.0f + 0.5f);
        }
    S3D_FREE(lum);
    S3D_FREE(t->bump);
    t->bump = bump;
    t->version++;
    return 1;
}

/* The heights from a picture of another size: `data` holds `channels` bytes per pixel (1: heights, 3 or 4: colours, whose brightness is the height); it is scaled to this texture. */
static int s3d_texSetBump(S3dTexture* t, const uint8_t* data, int dw, int dh, int channels) {
    int x, y;
    uint8_t* bump = (uint8_t*)S3D_MALLOC((s3d_size)t->w * (s3d_size)t->h + 1);
    if (bump == NULL) return 0;
    for (y = 0; y < t->h; y++)
        for (x = 0; x < t->w; x++) {
            int sx = s3d_mini(dw - 1, (int)((int64_t)x * dw / t->w)), sy = s3d_mini(dh - 1, (int)((int64_t)y * dh / t->h));
            const uint8_t* p = data + ((s3d_size)sy * (s3d_size)dw + (s3d_size)sx) * (s3d_size)channels;
            float v = channels == 1 ? (float)p[0] : 0.299f * p[0] + 0.587f * p[1] + 0.114f * p[2];
            bump[(s3d_size)y * (s3d_size)t->w + (s3d_size)x] = (uint8_t)s3d_minf(255.0f, v + 0.5f);
        }
    S3D_FREE(t->bump);
    t->bump = bump;
    t->version++;
    return 1;
}

static void s3d_texClearBump(S3dTexture* t) { S3D_FREE(t->bump); t->bump = NULL; t->version++; }

/* ---- lights, camera, surfaces ------------------------------------------------------------------------------------------------------------ */
enum { S3D_LIGHT_AMBIENT = 0, S3D_LIGHT_DIRECTIONAL = 1, S3D_LIGHT_POINT = 2 };
typedef struct S3dLight {
    int kind;
    S3dVec3 color;
    float intensity;
    S3dVec3 pos;          /* point */
    S3dVec3 dir;          /* directional: the direction the light travels */
    float range;          /* point: the distance at which it has half its strength */
    float cutoff;         /* point: from this distance on it lights nothing (the light fades to 0 there, smoothly); 0: no end */
    int castShadows;
    int mask;             /* the layers of objects that it lights (an object has its layers, too: they have to share one) */
} S3dLight;

typedef struct S3dCamera {
    S3dVec3 pos;
    float rx, ry, rz;
    float fovY;
    float nearPlane, farPlane;
} S3dCamera;

/* What a material and the object's look decide: the base colour, the texture mapping (2x3 matrix on the texture coordinates), the surface. */
typedef struct S3dSurface {
    S3dVec3 color;
    float specular, shininess;
    int smooth, twoSided;
    S3dVec3 emissive;
    int useTexture;
    float tm[6];          /* u' = a*u + b*v + tx ; v' = c*u + d*v + ty  stored as {a, b, tx, c, d, ty} */
    float opacity;        /* below 1 (or a texture with transparent texels): blended with what is behind */
    float cutoff;         /* > 0: a texel with a smaller alpha is not drawn at all (a cut-out: leaves, fences), and the rest is opaque */
    int lit;              /* 0: no lighting, the colour as it is (and the emissive part) */
    int castShadows, receiveShadows;
    int layers;
    float bump;           /* the strength of the bump map of the texture (0: none) */
    int billboard;        /* 0 none, 1 faces the camera, 2 turns around the vertical axis only */
    const S3dTexture* emissiveTex;   /* an emission map (NULL: none): its colour (times its alpha) times emissiveStrength is added to the lit colour, at the texture coordinates of the texture */
    float emissiveStrength;
    float reflect;        /* 0..1: how much the surface mirrors what is above it (a screen space reflection of the software renderer; opaque surfaces that face up) */
} S3dSurface;

static S3dSurface s3d_surfaceDefault(void) {
    S3dSurface s;
    memset(&s, 0, sizeof s);
    s.color = s3d_v3(0.8f, 0.8f, 0.8f);
    s.shininess = 32;
    s.smooth = 1;
    s.tm[0] = 1; s.tm[4] = 1;
    s.opacity = 1;
    s.lit = 1;
    s.castShadows = 1; s.receiveShadows = 1;
    s.layers = -1;
    return s;
}

typedef struct S3dObjectParams {
    S3dVec3 pos, scale, pivot;   /* the pivot is a point of the mesh (in mesh space) that sits at `pos` */
    float rx, ry, rz;
    int hasMatrix;               /* 1: `model` and `normalM` are given (a part of a model: the object's matrix times the part's) and the fields above are not used */
    S3dMat4 model, normalM;
} S3dObjectParams;

static void s3d_clearPixels(uint8_t* rgba, int w, int h, uint32_t color) {
    uint8_t bytes[4];
    uint32_t packed;
    uint32_t* p = (uint32_t*)(void*)rgba;
    s3d_size i, n = (s3d_size)w * (s3d_size)h;
    bytes[0] = (uint8_t)(color & 255); bytes[1] = (uint8_t)((color >> 8) & 255); bytes[2] = (uint8_t)((color >> 16) & 255); bytes[3] = (uint8_t)((color >> 24) & 255);
    memcpy(&packed, bytes, 4);
    for (i = 0; i < n; i++) p[i] = packed;
}

/* A vertex while it is clipped and rasterised: clip space position and the attributes that are interpolated. */
typedef struct S3dClipVertex {
    float x, y, z, w;
    float wx, wy, wz;   /* world position */
    float nx, ny, nz;   /* world normal */
    float u, v;
} S3dClipVertex;

static inline float s3d_lerp(float p, float q, float t) { return p + (q - p) * t; }
static S3dClipVertex s3d_lerpVertex(const S3dClipVertex* a, const S3dClipVertex* b, float t) {
    S3dClipVertex r;
    r.x = s3d_lerp(a->x, b->x, t); r.y = s3d_lerp(a->y, b->y, t); r.z = s3d_lerp(a->z, b->z, t); r.w = s3d_lerp(a->w, b->w, t);
    r.wx = s3d_lerp(a->wx, b->wx, t); r.wy = s3d_lerp(a->wy, b->wy, t); r.wz = s3d_lerp(a->wz, b->wz, t);
    r.nx = s3d_lerp(a->nx, b->nx, t); r.ny = s3d_lerp(a->ny, b->ny, t); r.nz = s3d_lerp(a->nz, b->nz, t);
    r.u = s3d_lerp(a->u, b->u, t); r.v = s3d_lerp(a->v, b->v, t);
    return r;
}

static inline int s3d_floorInt(float x) { int i = (int)x; return i - (x < (float)i ? 1 : 0); }

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * The frame: everything a camera draws is first QUEUED (an object with its matrices) and then drawn in one go by `s3d_flush`. The software renderer transforms the objects and cuts their
 * triangles to the screen in the order they were queued, then fills the rows of the picture: they are shared between threads, every pixel belongs to one of them and gets its triangles in
 * the order they were queued, so the picture does not depend on the number of threads.
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
#define S3D_ATTRS 10   /* z, 1/w, u/w, v/w, normal/w (3), position/w (3) */

/* One object drawn into the frame: what its triangles share. */
typedef struct S3dBatch {
    S3dSurface s;
    const S3dTexture* tex;
    const S3dMesh* mesh;
    S3dCamera cam;               /* the camera it was queued for (a billboard faces it) */
    S3dMat4 model, normalM, vp;  /* model -> world, the normal matrix (R * S^-1), world -> clip space (OpenGL style: -w <= z <= w) */
    float sortKey;               /* the distance to the camera: blended objects are drawn from far to near */
    int blended;
    int bumpWanted;
    S3dClipVertex* cv; s3d_size cvN, cvCap;   /* software: the vertices in world space, with the transformed texture coordinates and the clip space position */
} S3dBatch;

/* A triangle ready to be filled: its place on the screen and the planes of the attributes (value at vertex 0 and the change per pixel in x and y). */
typedef struct S3dTri {
    int batch;
    int minY, maxY;
    float sx[3], sy[3];
    float base[S3D_ATTRS], gx[S3D_ATTRS], gy[S3D_ATTRS];
    S3dVec3 fn;                 /* the face normal (world) */
    int constNormal;
    int bump;
    S3dVec3 tangent, bitangent; /* world directions of increasing u and v (for bump mapping) */
} S3dTri;

typedef struct S3dShadowMap {
    int res;
    int ortho;
    S3dMat4 view;                /* world -> light view (looking down -z) */
    float sx, sy, ox, oy;        /* ortho: u = x*sx + ox ; v = y*sy + oy */
    float f;                     /* perspective: u = x/d*f*0.5 + 0.5 */
    float* depth; s3d_size depthCap;
} S3dShadowMap;

typedef struct S3dShadowSet {
    int count;                   /* 1 for a directional light, 6 faces for a point light */
    S3dShadowMap maps[6];
} S3dShadowSet;

/* A light as the pixels use it. */
typedef struct S3dPreparedLight {
    int kind;
    S3dVec3 color;       /* colour times intensity */
    S3dVec3 pos;
    S3dVec3 toLight;     /* directional: the unit direction towards the light */
    float invRange;
    float invCutoff2;    /* point: 1 / cutoff^2 (0: the light has no end) */
    int mask;
    int shadow;          /* index into the shadow sets, -1: casts none */
} S3dPreparedLight;

/* The end of a point light (Light.Cutoff): `r2` is (distance / cutoff)^2 (below 1). The attenuation is multiplied by (1 - (d/c)^4)^2: 1 at the light, falling
 * smoothly to exactly 0 at the cutoff (no edge, also not in its slope). */
static inline float s3d_cutoffWindow(float r2) { float w = 1.0f - r2 * r2; return w * w; }

/* The point lights with an end are culled per tile of the picture (S3D_TILE x S3D_TILE pixels): a tile lists the lights that can reach one of its pixels - every
 * light without an end, and the point lights whose sphere (pos, cutoff) covers part of the tile on the screen - in the order of the lights, so a pixel sums the
 * same lights in the same order as without culling (the same picture). */
#define S3D_TILE_SHIFT 4
#define S3D_TILE (1 << S3D_TILE_SHIFT)

typedef struct S3dContext {
    S3dVec3 eye;
    S3dVec3 ambient;
    S3dPreparedLight* lights; s3d_size lightN, lightCap;
    S3dShadowSet* shadows; s3d_size shadowCap, shadowInit;   /* the depth buffers are kept from frame to frame (shadowInit: the sets that were ever used) */
    int shadowN;
    int culled;                                              /* the lights are culled per tile: a pixel looks at the lights of its tile only */
    int tilesX, tilesY;
    int* tileStart; s3d_size tileStartCap;                   /* tile t: its lights are tileLights[tileStart[t] .. tileStart[t + 1] - 1] (indices into `lights`) */
    int* tileLights; s3d_size tileLightCap;
} S3dContext;

typedef struct S3dFlushSettings {
    int shadows;               /* 0: no shadow maps at all */
    int shadowResolution;
    int threads;               /* 0: as many as the machine has (up to 8), 1: none */
    int backend;               /* S3D_BACKEND_*: who draws */
    int noLightCulling;        /* 1: every pixel looks at every light (the same picture, slower: for comparisons) */
    uint32_t sky;              /* 0xAABBGGRR: what a reflection shows where it finds nothing on the screen (the background, or the fog) */
    float fogStart, fogEnd;    /* the fog of the scene (end > start), for the hardware renderer: it draws it itself (the software one gets it from s3d_fxApply afterwards) */
    uint32_t fogColor;         /* 0xAABBGGRR */
} S3dFlushSettings;
enum { S3D_BACKEND_SOFTWARE = 0, S3D_BACKEND_HARDWARE = 1, S3D_BACKEND_DIRECT = 2 };   /* (DIRECT: who drew - the GPU, into the windows of the camera; never asked for) */

typedef struct S3dFrame {
    int w, h;
    float* depth;
    uint32_t clearColor;       /* the colour of FrameClear (the hardware backend clears with it) */
    S3dBatch* batches; s3d_size batchN, batchCap, batchInit;   /* batchInit: the slots that were ever used (their vertex memory is kept) */
    S3dTri* opaque; s3d_size opaqueN, opaqueCap;
    S3dTri* blended; s3d_size blendedN, blendedCap;
    int* order; s3d_size orderCap; int* orderTmp; s3d_size orderTmpCap;   /* the blended triangles from far to near */
    S3dContext ctx;
    int lastBackend;           /* who drew the last picture (S3D_BACKEND_*) */
    void* gpu;                 /* the targets of the hardware backend, or NULL */
    void* direct;              /* DirectDrawing: the windows the camera draws into (s3d_gpu_attach), or NULL */
    /* reflections (only while a surface of the picture reflects): per pixel how much it reflects (0: not) and its normal (octahedral), and a copy of the picture before them */
    int reflectOn;
    float* reflect; s3d_size reflectCap;
    int8_t* reflectN; s3d_size reflectNCap;
    uint8_t* reflectSrc; s3d_size reflectSrcCap;
    int* skyline; s3d_size skylineCap; int skyLevels;   /* the first row with something drawn in each column, and the smallest of 2^k columns from each (level k) */
    /* the fog (software, a picture with transparent objects): it goes over the opaque objects before the transparent ones are drawn, and each of these is fogged by its own
     * distance as it is drawn - as the hardware renderer does it; fogDone: this picture has its fog (s3d_fxApply leaves it) */
    int fogOn, fogDone;
    float fogStart, fogEnd, fogZA, fogZB, fogZC, fogColor[3];
} S3dFrame;

#ifndef FIRE_S3D_LEAN_ONLY   /* (a build with the lean renderer only, s3d_lean.h: the frames, shadows and rows of the full renderer are not in it) */
static void s3d_gpu_releaseFrame(S3dFrame* f);

static void s3d_frameEmpty(S3dFrame* f) { f->batchN = 0; f->opaqueN = 0; f->blendedN = 0; }
static int s3d_frameResize(S3dFrame* f, int nw, int nh) {
    s3d_size n = (s3d_size)nw * (s3d_size)nh, i;
    float* d = (float*)S3D_MALLOC(n * sizeof(float) + 1);
    if (d == NULL) return 0;
    for (i = 0; i < n; i++) d[i] = 1e30f;
    S3D_FREE(f->depth);
    f->depth = d;
    f->w = nw; f->h = nh;
    s3d_frameEmpty(f);
    return 1;
}
static void s3d_frameClearDepth(S3dFrame* f) {
    s3d_size i, n = (s3d_size)f->w * (s3d_size)f->h;
    for (i = 0; i < n; i++) f->depth[i] = 1e30f;
    s3d_frameEmpty(f);
}
static S3dFrame* s3d_frameNew(int w, int h) {
    S3dFrame* f = (S3dFrame*)s3d_calloc(1, sizeof(S3dFrame));
    if (f == NULL) return NULL;
    if (!s3d_frameResize(f, w, h)) { S3D_FREE(f); return NULL; }
    return f;
}
static void s3d_frameFree(S3dFrame* f) {
    s3d_size i;
    int k, j;
    if (f == NULL) return;
    s3d_gpu_releaseFrame(f);
    for (i = 0; i < f->batchInit; i++) S3D_FREE(f->batches[i].cv);
    S3D_FREE(f->batches); S3D_FREE(f->opaque); S3D_FREE(f->blended); S3D_FREE(f->order); S3D_FREE(f->orderTmp); S3D_FREE(f->depth);
    S3D_FREE(f->ctx.lights); S3D_FREE(f->ctx.tileStart); S3D_FREE(f->ctx.tileLights);
    S3D_FREE(f->reflect); S3D_FREE(f->reflectN); S3D_FREE(f->reflectSrc); S3D_FREE(f->skyline);
    for (k = 0; k < (int)f->ctx.shadowInit; k++) for (j = 0; j < 6; j++) S3D_FREE(f->ctx.shadows[k].maps[j].depth);
    S3D_FREE(f->ctx.shadows);
    S3D_FREE(f);
}

static int s3d_shadowProject(const S3dShadowMap* m, S3dVec3 p, float* u, float* v, float* d) {
    S3dVec3 q = s3d_transformPoint(&m->view, p);
    float k;
    *d = -q.z;
    if (m->ortho) { *u = q.x * m->sx + m->ox; *v = 1.0f - (q.y * m->sy + m->oy); return 1; }
    if (*d <= 1e-6f) return 0;
    k = m->f * 0.5f / *d;
    *u = q.x * k + 0.5f; *v = 1.0f - (q.y * k + 0.5f);
    return 1;
}

/* How much of the light reaches `p` (0 = in the shadow, 1 = lit): the depth in the shadow map of the light, compared at the four texels around the point and weighted bilinearly (a soft edge). */
static float s3d_shadowFactor(const S3dShadowSet* set, S3dVec3 lightPos, int directional, S3dVec3 p, S3dVec3 n) {
    const S3dShadowMap* map = &set->maps[0];
    float u, v, depth, texel, bias, fx, fy, tx, ty, limit, s00, s10, s01, s11;
    int x0, y0, x1, y1, last;
    const float* d;
    s3d_size R;
    S3dVec3 q;
    if (!directional) {
        S3dVec3 dd = s3d_sub(p, lightPos);
        float ax = fabsf(dd.x), ay = fabsf(dd.y), az = fabsf(dd.z);
        int face = ax >= ay && ax >= az ? (dd.x >= 0 ? 0 : 1) : ay >= az ? (dd.y >= 0 ? 2 : 3) : (dd.z >= 0 ? 4 : 5);
        map = &set->maps[face];
    }
    if (map->ortho) texel = 1.0f / (map->sx * (float)map->res);
    else {
        if (!s3d_shadowProject(map, p, &u, &v, &depth)) return 1.0f;
        texel = 2.0f * depth / (map->f * (float)map->res);
    }
    /* move the point along its normal by about a texel: a surface does not shadow itself */
    q = s3d_add(p, s3d_scale(n, texel * 1.5f));
    if (!s3d_shadowProject(map, q, &u, &v, &depth)) return 1.0f;
    bias = texel * 1.0f + 0.002f;
    if (u < 0 || v < 0 || u >= 1 || v >= 1) return 1.0f;
    fx = u * (float)map->res - 0.5f; fy = v * (float)map->res - 0.5f;
    x0 = s3d_floorInt(fx); y0 = s3d_floorInt(fy);
    tx = fx - (float)x0; ty = fy - (float)y0;
    last = map->res - 1;
    x1 = s3d_mini(last, x0 + 1); y1 = s3d_mini(last, y0 + 1);
    x0 = s3d_maxi(0, x0); y0 = s3d_maxi(0, y0);
    d = map->depth;
    R = (s3d_size)map->res;
    limit = depth - bias;
    s00 = limit <= d[(s3d_size)y0 * R + (s3d_size)x0] ? 1.0f : 0.0f; s10 = limit <= d[(s3d_size)y0 * R + (s3d_size)x1] ? 1.0f : 0.0f;
    s01 = limit <= d[(s3d_size)y1 * R + (s3d_size)x0] ? 1.0f : 0.0f; s11 = limit <= d[(s3d_size)y1 * R + (s3d_size)x1] ? 1.0f : 0.0f;
    return (s00 * (1 - tx) + s10 * tx) * (1 - ty) + (s01 * (1 - tx) + s11 * tx) * ty;
}

/* The four texels around (u, v) in repeat mode and the weights: v = 0 is the top row of the picture. */
typedef struct S3dTexels { s3d_size i00, i10, i01, i11; float tx, ty; } S3dTexels;
static inline S3dTexels s3d_texels(int w, int h, float u, float v) {
    S3dTexels r;
    float x = (u - (float)s3d_floorInt(u)) * (float)w - 0.5f, y = (v - (float)s3d_floorInt(v)) * (float)h - 0.5f;
    int x0 = s3d_floorInt(x), y0 = s3d_floorInt(y), x1, y1;
    r.tx = x - (float)x0; r.ty = y - (float)y0;
    x1 = x0 + 1; y1 = y0 + 1;
    if (x0 < 0) x0 += w;
    if (y0 < 0) y0 += h;
    if (x1 >= w) x1 -= w;
    if (y1 >= h) y1 -= h;
    if (x0 >= w) x0 = w - 1;
    if (y0 >= h) y0 = h - 1;
    r.i00 = (s3d_size)y0 * (s3d_size)w + (s3d_size)x0; r.i10 = (s3d_size)y0 * (s3d_size)w + (s3d_size)x1;
    r.i01 = (s3d_size)y1 * (s3d_size)w + (s3d_size)x0; r.i11 = (s3d_size)y1 * (s3d_size)w + (s3d_size)x1;
    return r;
}

/* Texture coordinates in repeat mode, bilinear. `alpha` is the transparency of the point (0..1). */
static inline S3dVec3 s3d_sampleTexels(const S3dTexture* t, S3dTexels q, float* alpha) {
    const uint8_t *p00 = &t->rgba[q.i00 * 4], *p10 = &t->rgba[q.i10 * 4], *p01 = &t->rgba[q.i01 * 4], *p11 = &t->rgba[q.i11 * 4];
    float w00 = (1 - q.tx) * (1 - q.ty), w10 = q.tx * (1 - q.ty), w01 = (1 - q.tx) * q.ty, w11 = q.tx * q.ty;
    const float k = 1.0f / 255.0f;
    *alpha = (p00[3] * w00 + p10[3] * w10 + p01[3] * w01 + p11[3] * w11) * k;
    return s3d_v3((p00[0] * w00 + p10[0] * w10 + p01[0] * w01 + p11[0] * w11) * k, (p00[1] * w00 + p10[1] * w10 + p01[1] * w01 + p11[1] * w11) * k,
                  (p00[2] * w00 + p10[2] * w10 + p01[2] * w01 + p11[2] * w11) * k);
}
static inline S3dVec3 s3d_sampleTexture(const S3dTexture* t, float u, float v, float* alpha) { return s3d_sampleTexels(t, s3d_texels(t->w, t->h, u, v), alpha); }

/* One coordinate of sharp bilinear (in texels, the centres at i + 0.5): within a texel it stays on its value, only the last `fw` texels (one screen pixel) before the edge blend into the next. When a
 * texel is smaller than a pixel it is plain bilinear. */
static inline float s3d_sharpen(float p, float fw) {
    float seam = (float)s3d_floorInt(p + 0.5f), d = fw < 1.0f ? fw : 1.0f, r;
    if (d < 1e-6f) d = 1e-6f;
    r = (p - seam) / d;
    if (r < -0.5f) r = -0.5f;
    if (r > 0.5f) r = 0.5f;
    return seam + r;
}

/* The colour with the texture's filter. `fu`, `fv`: how many texels one screen pixel covers along u and v (for sharp bilinear). */
static inline S3dVec3 s3d_sampleFiltered(const S3dTexture* t, float u, float v, float fu, float fv, float* alpha) {
    if (t->filter == S3D_FILTER_NEAREST) {
        S3dTexels q;
        int x = (int)((u - (float)s3d_floorInt(u)) * (float)t->w), y = (int)((v - (float)s3d_floorInt(v)) * (float)t->h);
        if (x >= t->w) x = t->w - 1;
        if (y >= t->h) y = t->h - 1;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        q.i00 = q.i10 = q.i01 = q.i11 = (s3d_size)y * (s3d_size)t->w + (s3d_size)x;
        q.tx = q.ty = 0;
        return s3d_sampleTexels(t, q, alpha);
    }
    if (t->filter == S3D_FILTER_SHARP) {
        float px = (u - (float)s3d_floorInt(u)) * (float)t->w, py = (v - (float)s3d_floorInt(v)) * (float)t->h;
        px = s3d_sharpen(px, fu); py = s3d_sharpen(py, fv);
        return s3d_sampleTexels(t, s3d_texels(t->w, t->h, px / (float)t->w, py / (float)t->h), alpha);
    }
    return s3d_sampleTexture(t, u, v, alpha);
}

static inline float s3d_sampleBump(const S3dTexture* t, float u, float v) {
    S3dTexels q = s3d_texels(t->w, t->h, u, v);
    const uint8_t* b = t->bump;
    float a = b[q.i00] * (1 - q.tx) + b[q.i10] * q.tx;
    float c = b[q.i01] * (1 - q.tx) + b[q.i11] * q.tx;
    return (a * (1 - q.ty) + c * q.ty) * (1.0f / 255.0f);
}

static inline uint8_t s3d_toByte(float c) { return (uint8_t)s3d_minf(255.0f, s3d_maxf(0.0f, c * 255.0f + 0.5f)); }

/* Fills one triangle into the rows y0..y1 of the frame. Pixels are tested in the middle: a pixel belongs to the triangle when its centre is inside (the left and top edges count, the right and
 * bottom ones do not, so two triangles that share an edge never draw the same pixel twice). */
/* The fog at a depth of the picture (NDC z; 1e30 nothing - all fog): 0 none .. 1 all fog, as s3d_fxApply has it. */
static float s3d_frameFog(const S3dFrame* f, float z) {
    float d;
    if (z >= 1e29f) return 1.0f;
    d = f->fogZC / (z * f->fogZA + f->fogZB);
    if (d <= 0) return 1.0f;
    return d <= f->fogStart ? 0.0f : d >= f->fogEnd ? 1.0f : (d - f->fogStart) / (f->fogEnd - f->fogStart);
}
/* An alpha from this on counts as opaque in the pass of the transparent objects (it is drawn and hides what is behind it, as an opaque object does): 255 of 255, as the
 * bilinear filter of opaque texels gives it within the rounding of floats. The hardware renderer uses the same limit (s3d_main.frag). */
#define S3D_OPAQUE_ALPHA (1.0f - 0.5f / 255.0f)

static void s3d_rasterTri(uint8_t* rgba, S3dFrame* f, const S3dContext* ctx, const S3dTri* t, int y0, int y1, int blendedPass) {
    const S3dBatch* b = &f->batches[t->batch];
    const S3dSurface* s = &b->s;
    const S3dTexture* tex = b->tex;
    const S3dTexture* glow = s->emissiveTex;
    int texOn = s->useTexture && tex && tex->w > 0 && tex->h > 0;
    int bumpOn = t->bump && texOn && tex->bump != NULL;
    float alphaScale = s->opacity;
    int rowStart = s3d_maxi(t->minY, y0), rowEnd = s3d_mini(t->maxY, y1);
    int y, x, e, k;
    for (y = rowStart; y <= rowEnd; y++) {
        float py = (float)y + 0.5f;
        /* the span of the row: where the edges cross it */
        float xl = 1e30f, xr = -1e30f, dx0, dy0;
        float cur[S3D_ATTRS];
        int crossings = 0, xs, xe;
        s3d_size di;
        for (e = 0; e < 3; e++) {
            int j = (e + 1) % 3;
            float ya = t->sy[e], yb = t->sy[j], lo, hi, cx;
            if (ya == yb) continue;
            lo = ya < yb ? ya : yb; hi = ya < yb ? yb : ya;
            if (py < lo || py >= hi) continue;
            cx = t->sx[e] + (py - ya) * (t->sx[j] - t->sx[e]) / (yb - ya);
            if (cx < xl) xl = cx;
            if (cx > xr) xr = cx;
            crossings++;
        }
        if (crossings < 2) continue;
        xs = s3d_maxi(0, (int)ceilf(xl - 0.5f)); xe = s3d_mini(f->w - 1, (int)ceilf(xr - 0.5f) - 1);
        if (xs > xe) continue;
        dx0 = (float)xs + 0.5f - t->sx[0]; dy0 = py - t->sy[0];
        for (k = 0; k < S3D_ATTRS; k++) cur[k] = t->base[k] + t->gx[k] * dx0 + t->gy[k] * dy0;
        di = (s3d_size)y * (s3d_size)f->w + (s3d_size)xs;
        for (x = xs; x <= xe; x++, di++) {
            float z = cur[0];
            int visible = z < f->depth[di] && cur[1] > 0;
            if (visible) {
                float kk = 1.0f / cur[1];
                S3dVec3 p = s3d_v3(cur[7] * kk, cur[8] * kk, cur[9] * kk);
                S3dVec3 n = t->fn, base, glowAdd = s3d_v3(0, 0, 0);
                float alpha, u = 0, v = 0;
                if (s->smooth && !t->constNormal) {
                    float nx = cur[4] * kk, ny = cur[5] * kk, nz = cur[6] * kk;
                    float l2 = nx * nx + ny * ny + nz * nz;
                    float inv = l2 > 1e-20f ? 1.0f / sqrtf(l2) : 0.0f;
                    n = s3d_v3(nx * inv, ny * inv, nz * inv);
                }
                if (s->twoSided && s3d_dot(n, s3d_sub(ctx->eye, p)) < 0) n = s3d_scale(n, -1.0f);
                base = s->color;
                alpha = alphaScale;
                if (texOn) {
                    float ta;
                    S3dVec3 tc;
                    u = cur[2] * kk; v = cur[3] * kk;
                    if (tex->filter == S3D_FILTER_SHARP) {
                        /* the texels under one screen pixel: the derivatives of u = (u/w) / (1/w) along x and y */
                        float uxd = (t->gx[2] - u * t->gx[1]) * kk, uyd = (t->gy[2] - u * t->gy[1]) * kk;
                        float vxd = (t->gx[3] - v * t->gx[1]) * kk, vyd = (t->gy[3] - v * t->gy[1]) * kk;
                        tc = s3d_sampleFiltered(tex, u, v, (fabsf(uxd) + fabsf(uyd)) * (float)tex->w, (fabsf(vxd) + fabsf(vyd)) * (float)tex->h, &ta);
                    }
                    else tc = s3d_sampleFiltered(tex, u, v, 1, 1, &ta);
                    if (s->cutoff > 0 && ta < s->cutoff) visible = 0;
                    base = s3d_v3(base.x * tc.x, base.y * tc.y, base.z * tc.z);
                    alpha *= ta;
                    if (visible && bumpOn) {
                        /* the slope of the heights along u and v over one texel (the strength says how high a full step of the heights is): tilt the normal against it */
                        float du = 0.5f / (float)tex->w, dv = 0.5f / (float)tex->h;
                        float hu = (s3d_sampleBump(tex, u + du, v) - s3d_sampleBump(tex, u - du, v)) * s->bump;
                        float hv = (s3d_sampleBump(tex, u, v + dv) - s3d_sampleBump(tex, u, v - dv)) * s->bump;
                        S3dVec3 tilted = s3d_sub(s3d_sub(n, s3d_scale(t->tangent, hu)), s3d_scale(t->bitangent, hv));
                        n = s3d_normalize(tilted);
                    }
                }
                if (visible && glow) {
                    /* the emission map: at the same texture coordinates (with its own filter) */
                    float ga;
                    S3dVec3 gc;
                    if (!texOn) { u = cur[2] * kk; v = cur[3] * kk; }
                    if (glow->filter == S3D_FILTER_SHARP) {
                        float uxd = (t->gx[2] - u * t->gx[1]) * kk, uyd = (t->gy[2] - u * t->gy[1]) * kk;
                        float vxd = (t->gx[3] - v * t->gx[1]) * kk, vyd = (t->gy[3] - v * t->gy[1]) * kk;
                        gc = s3d_sampleFiltered(glow, u, v, (fabsf(uxd) + fabsf(uyd)) * (float)glow->w, (fabsf(vxd) + fabsf(vyd)) * (float)glow->h, &ga);
                    }
                    else gc = s3d_sampleFiltered(glow, u, v, 1, 1, &ga);
                    ga *= s->emissiveStrength;
                    glowAdd = s3d_v3(gc.x * ga, gc.y * ga, gc.z * ga);
                }
                if (visible) {
                    S3dVec3 col;
                    uint8_t* px;
                    if (!s->lit) col = s3d_v3(base.x + s->emissive.x, base.y + s->emissive.y, base.z + s->emissive.z);
                    else {
                        S3dVec3 diffuse = ctx->ambient, spec = s3d_v3(0, 0, 0), viewDir = s3d_v3(0, 0, 0);
                        int haveView = 0;
                        s3d_size li, lightCount = ctx->lightN;
                        const int* tileList = NULL;
                        if (ctx->culled) {   /* only the lights that can reach the tile of the pixel */
                            int ti = (y >> S3D_TILE_SHIFT) * ctx->tilesX + (x >> S3D_TILE_SHIFT);
                            tileList = ctx->tileLights + ctx->tileStart[ti];
                            lightCount = (s3d_size)(ctx->tileStart[ti + 1] - ctx->tileStart[ti]);
                        }
                        for (li = 0; li < lightCount; li++) {
                            const S3dPreparedLight* L = &ctx->lights[tileList ? (s3d_size)tileList[li] : li];
                            S3dVec3 toLight;
                            float atten = 1.0f, nl, shadow, w;
                            if (!(L->mask & s->layers)) continue;
                            if (L->kind == S3D_LIGHT_DIRECTIONAL) toLight = L->toLight;
                            else {
                                S3dVec3 d = s3d_sub(L->pos, p);
                                float d2 = d.x * d.x + d.y * d.y + d.z * d.z;
                                float dist, q, r2 = 0;
                                if (L->invCutoff2 > 0) { r2 = d2 * L->invCutoff2; if (r2 >= 1.0f) continue; }   /* beyond its end */
                                dist = sqrtf(d2);
                                toLight = dist > 1e-12f ? s3d_scale(d, 1.0f / dist) : s3d_v3(0, 1, 0);
                                q = dist * L->invRange;
                                atten = 1.0f / (1.0f + q * q);
                                if (L->invCutoff2 > 0) atten *= s3d_cutoffWindow(r2);
                            }
                            nl = s3d_dot(n, toLight);
                            if (nl <= 0) continue;
                            shadow = 1.0f;
                            if (L->shadow >= 0 && s->receiveShadows) shadow = s3d_shadowFactor(&ctx->shadows[L->shadow], L->pos, L->kind == S3D_LIGHT_DIRECTIONAL, p, n);
                            if (shadow <= 0) continue;
                            w = atten * shadow;
                            diffuse = s3d_add(diffuse, s3d_scale(L->color, nl * w));
                            if (s->specular > 0) {
                                S3dVec3 hh;
                                float nh;
                                if (!haveView) { viewDir = s3d_normalize(s3d_sub(ctx->eye, p)); haveView = 1; }
                                hh = s3d_normalize(s3d_add(toLight, viewDir));
                                nh = s3d_dot(n, hh);
                                if (nh > 0) spec = s3d_add(spec, s3d_scale(L->color, w * s->specular * powf(nh, s->shininess)));
                            }
                        }
                        col = s3d_v3(base.x * diffuse.x + spec.x + s->emissive.x, base.y * diffuse.y + spec.y + s->emissive.y, base.z * diffuse.z + spec.z + s->emissive.z);
                    }
                    if (glow) col = s3d_add(col, glowAdd);
                    if (blendedPass && f->fogOn) {   /* a transparent object: the fog of its own distance (what is behind it has its fog already) */
                        float k = s3d_frameFog(f, z);
                        col = s3d_v3(col.x + (f->fogColor[0] - col.x) * k, col.y + (f->fogColor[1] - col.y) * k, col.z + (f->fogColor[2] - col.z) * k);
                    }
                    px = rgba + di * 4;
                    if (blendedPass && alpha < S3D_OPAQUE_ALPHA && s->cutoff <= 0) {
                        if (alpha > 0) {
                            uint8_t cr = s3d_toByte(col.x), cg = s3d_toByte(col.y), cb = s3d_toByte(col.z);
                            px[0] = (uint8_t)(px[0] + (cr - (int)px[0]) * alpha + (cr >= px[0] ? 0.5f : -0.5f));
                            px[1] = (uint8_t)(px[1] + (cg - (int)px[1]) * alpha + (cg >= px[1] ? 0.5f : -0.5f));
                            px[2] = (uint8_t)(px[2] + (cb - (int)px[2]) * alpha + (cb >= px[2] ? 0.5f : -0.5f));
                        }
                    } else {
                        px[0] = s3d_toByte(col.x); px[1] = s3d_toByte(col.y); px[2] = s3d_toByte(col.z); px[3] = 255;
                        f->depth[di] = z;
                        if (f->reflectOn) {   /* what the reflection pass needs: how much the pixel reflects, its normal */
                            f->reflect[di] = s->reflect;
                            if (s->reflect > 0) s3d_octEncode(n, f->reflectN + di * 2);
                        }
                    }
                }
            }
            for (k = 0; k < S3D_ATTRS; k++) cur[k] += t->gx[k];
        }
    }
}

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * Queueing: the matrices of an object. Setting up (software): transform a mesh, cut it against the near plane, put its triangles on the screen
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
typedef struct S3dScreenVertex {
    float sx, sy, z, invW;
    float wx, wy, wz, nx, ny, nz, u, v;   /* attributes divided by w (for perspective correction) */
} S3dScreenVertex;

/* The plane of one attribute through three screen points: the value at vertex 0 and the change per pixel. */
static inline void s3d_planeOf(const float* sx, const float* sy, float a0, float a1, float a2, float invDet, float* base, float* gx, float* gy) {
    float d1 = a1 - a0, d2 = a2 - a0;
    float x1 = sx[1] - sx[0], x2 = sx[2] - sx[0], y1 = sy[1] - sy[0], y2 = sy[2] - sy[0];
    *base = a0;
    *gx = (d1 * y2 - d2 * y1) * invDet;
    *gy = (d2 * x1 - d1 * x2) * invDet;
}

static int s3d_setupTri(S3dTri* t, const S3dScreenVertex* a, const S3dScreenVertex* b, const S3dScreenVertex* c, int height) {
    float det, lo, hi, inv;
    t->sx[0] = a->sx; t->sx[1] = b->sx; t->sx[2] = c->sx;
    t->sy[0] = a->sy; t->sy[1] = b->sy; t->sy[2] = c->sy;
    det = (t->sx[1] - t->sx[0]) * (t->sy[2] - t->sy[0]) - (t->sx[2] - t->sx[0]) * (t->sy[1] - t->sy[0]);
    if (fabsf(det) < 1e-9f) return 0;
    lo = s3d_min3f(a->sy, b->sy, c->sy); hi = s3d_max3f(a->sy, b->sy, c->sy);
    t->minY = s3d_maxi(0, (int)ceilf(lo - 0.5f));
    t->maxY = s3d_mini(height - 1, (int)ceilf(hi - 0.5f) - 1);
    if (t->minY > t->maxY) return 0;
    inv = 1.0f / det;
#define S3D_PLANE(k, field) s3d_planeOf(t->sx, t->sy, a->field, b->field, c->field, inv, &t->base[k], &t->gx[k], &t->gy[k])
    S3D_PLANE(0, z); S3D_PLANE(1, invW); S3D_PLANE(2, u); S3D_PLANE(3, v);
    S3D_PLANE(4, nx); S3D_PLANE(5, ny); S3D_PLANE(6, nz);
    S3D_PLANE(7, wx); S3D_PLANE(8, wy); S3D_PLANE(9, wz);
#undef S3D_PLANE
    return 1;
}

#endif /* FIRE_S3D_LEAN_ONLY */

/* The view of a camera: world -> camera space (the camera at the origin, looking along -Z). view = inverse(T(cam) * R) = R^-1 * T(-cam) */
static S3dMat4 s3d_viewMatrix(const S3dCamera* cam) {
    S3dMat4 rinv = s3d_mul(s3d_rotationZ(-cam->rz), s3d_mul(s3d_rotationX(-cam->rx), s3d_rotationY(-cam->ry)));
    return s3d_mul(rinv, s3d_translation(s3d_scale(cam->pos, -1.0f)));
}

/* The view and projection of a camera for a picture of w x h (OpenGL style clip space: -w <= z <= w). */
static S3dMat4 s3d_viewProjection(const S3dCamera* cam, int w, int h) {
    S3dMat4 view = s3d_viewMatrix(cam);
    float aspect = (float)w / (float)h;
    float tf = 1.0f / tanf(cam->fovY * 0.5f);
    float nearP = cam->nearPlane, farP = cam->farPlane;
    S3dMat4 proj;
    int i;
    for (i = 0; i < 16; i++) proj.m[i] = 0;
    S3D_AT(proj, 0, 0) = tf / aspect; S3D_AT(proj, 1, 1) = tf;
    S3D_AT(proj, 2, 2) = (farP + nearP) / (nearP - farP); S3D_AT(proj, 2, 3) = 2 * farP * nearP / (nearP - farP);
    S3D_AT(proj, 3, 2) = -1;
    return s3d_mul(proj, view);
}

/* The matrices of an object: model = T(pos) * R * S * T(-pivot) (a billboard replaces R by a turn towards the camera) and the normal matrix (R * S^-1, the inverse transpose of the
 * model's 3x3 part for a rotation and a non uniform scale). Both renderers place an object with them. */
static void s3d_objectMatrices(const S3dCamera* cam, const S3dObjectParams* obj, const S3dSurface* surf, S3dMat4* model, S3dMat4* normalM) {
    S3dMat4 rotation;
    S3dVec3 invS;
    if (obj->hasMatrix) { *model = obj->model; *normalM = obj->normalM; return; }
    rotation = s3d_rotationXYZ(obj->rx, obj->ry, obj->rz);
    if (surf->billboard != 0) {
        S3dVec3 toCam = s3d_sub(cam->pos, obj->pos), zaxis, xaxis, yaxis;
        S3dMat4 b = s3d_identity();
        if (surf->billboard == 2) toCam.y = 0;
        zaxis = s3d_normalize(toCam);
        xaxis = s3d_normalize(s3d_cross(s3d_v3(0, 1, 0), zaxis));
        if (fabsf(zaxis.y) > 0.9999f) xaxis = s3d_v3(1, 0, 0);
        yaxis = s3d_cross(zaxis, xaxis);
        S3D_AT(b, 0, 0) = xaxis.x; S3D_AT(b, 1, 0) = xaxis.y; S3D_AT(b, 2, 0) = xaxis.z;
        S3D_AT(b, 0, 1) = yaxis.x; S3D_AT(b, 1, 1) = yaxis.y; S3D_AT(b, 2, 1) = yaxis.z;
        S3D_AT(b, 0, 2) = zaxis.x; S3D_AT(b, 1, 2) = zaxis.y; S3D_AT(b, 2, 2) = zaxis.z;
        rotation = s3d_mul(b, s3d_rotationZ(obj->rz));
    }
    *model = s3d_mul(s3d_translation(obj->pos), s3d_mul(rotation, s3d_mul(s3d_scaling(obj->scale), s3d_translation(s3d_scale(obj->pivot, -1.0f)))));
    invS = s3d_v3(obj->scale.x != 0 ? 1.0f / obj->scale.x : 0, obj->scale.y != 0 ? 1.0f / obj->scale.y : 0, obj->scale.z != 0 ? 1.0f / obj->scale.z : 0);
    *normalM = s3d_mul(rotation, s3d_scaling(invS));
}

#ifndef FIRE_S3D_LEAN_ONLY   /* (the full renderer: queueing, set-up, threads) */
/* The bounds of the texture coordinates of a finished mesh (once: it does not change any more). */
static void s3d_meshUvBounds(S3dMesh* m) {
    s3d_size i, n = m->posN / 3;
    float u, v;
    if (m->uvKnown) return;
    m->uvLo[0] = m->uvLo[1] = 0; m->uvHi[0] = m->uvHi[1] = 0;
    for (i = 0; i < n; i++) {
        s3d_meshUv(m, i, &u, &v);
        if (i == 0 || u < m->uvLo[0]) m->uvLo[0] = u;
        if (i == 0 || u > m->uvHi[0]) m->uvHi[0] = u;
        if (i == 0 || v < m->uvLo[1]) m->uvLo[1] = v;
        if (i == 0 || v > m->uvHi[1]) m->uvHi[1] = v;
    }
    m->uvKnown = 1;
}
/* Whether a mesh with this surface can read a texel of `tex` that is not opaque: a texture with a transparent texel makes only the objects blended whose texture coordinates
 * (after the mapping of the surface, Object.TextureScale/Offset/Rotation) come near it - an atlas with a few panes of glass does not make every object of it transparent. */
static int s3d_meshReadsAlpha(const S3dMesh* mesh, const S3dSurface* surf, const S3dTexture* tex) {
    S3dMesh* m = (S3dMesh*)mesh;
    float lo[2], hi[2], cu[4], cv[4];
    int k;
    if (!tex->hasAlpha) return 0;
    s3d_meshUvBounds(m);
    /* the corners of the box of the coordinates through the mapping (an affine one: the box of the four corners holds every point) */
    for (k = 0; k < 4; k++) {
        float u = (k & 1) ? m->uvHi[0] : m->uvLo[0], v = (k & 2) ? m->uvHi[1] : m->uvLo[1];
        cu[k] = surf->tm[0] * u + surf->tm[1] * v + surf->tm[2];
        cv[k] = surf->tm[3] * u + surf->tm[4] * v + surf->tm[5];
    }
    lo[0] = hi[0] = cu[0]; lo[1] = hi[1] = cv[0];
    for (k = 1; k < 4; k++) {
        lo[0] = s3d_minf(lo[0], cu[k]); hi[0] = s3d_maxf(hi[0], cu[k]);
        lo[1] = s3d_minf(lo[1], cv[k]); hi[1] = s3d_maxf(hi[1], cv[k]);
    }
    return s3d_texReadsAlpha((S3dTexture*)tex, lo[0], hi[0], lo[1], hi[1]);
}

/* Queues one mesh for the frame: its matrices and its look; nothing is transformed or drawn before `s3d_flush`. The camera places the mesh (and a billboard faces it). The mesh and the
 * texture have to live until the flush. */
static void s3d_queueMesh(S3dFrame* f, const S3dCamera* cam, const S3dMesh* mesh, const S3dObjectParams* obj, const S3dSurface* surf, const S3dTexture* tex) {
    S3dMat4 model, normalM;
    S3dVec3 centre;
    S3dBatch* batch;
    if (f->w <= 0 || f->h <= 0 || mesh->idxN < 3) return;
    if (!S3D_GROW(f->batches, f->batchCap, f->batchN + 1)) return;
    s3d_objectMatrices(cam, obj, surf, &model, &normalM);

    /* a slot that was used before keeps its vertex memory; a new one starts empty */
    if (f->batchN >= f->batchInit) { memset(&f->batches[f->batchN], 0, sizeof(S3dBatch)); f->batchInit = f->batchN + 1; }
    batch = &f->batches[f->batchN];
    {
        S3dClipVertex* keep = batch->cv;
        s3d_size keepCap = batch->cvCap;
        memset(batch, 0, sizeof *batch);
        batch->cv = keep; batch->cvCap = keepCap;
    }
    f->batchN++;
    batch->s = *surf;
    batch->tex = (surf->useTexture && tex && tex->w > 0 && tex->h > 0) ? tex : NULL;
    if (surf->emissiveTex && (surf->emissiveTex->w <= 0 || surf->emissiveTex->h <= 0 || surf->emissiveStrength == 0)) batch->s.emissiveTex = NULL;
    batch->mesh = mesh;
    batch->cam = *cam;
    batch->model = model;
    batch->normalM = normalM;
    batch->vp = s3d_viewProjection(cam, f->w, f->h);
    batch->blended = surf->cutoff <= 0 && (surf->opacity < 1.0f || (batch->tex && s3d_meshReadsAlpha(mesh, surf, batch->tex)));
    centre = s3d_transformPoint(&model, s3d_scale(s3d_add(mesh->bmin, mesh->bmax), 0.5f));
    batch->sortKey = s3d_length(s3d_sub(centre, cam->pos));
    batch->bumpWanted = surf->bump != 0 && batch->tex && batch->tex->bump != NULL;
}

static S3dScreenVertex s3d_toScreen(const S3dClipVertex* c, int w, int h) {
    S3dScreenVertex s;
    float iw = 1.0f / c->w;
    s.sx = (c->x * iw * 0.5f + 0.5f) * (float)w;
    s.sy = (1.0f - (c->y * iw * 0.5f + 0.5f)) * (float)h;
    s.z = c->z * iw;
    s.invW = iw;
    s.wx = c->wx * iw; s.wy = c->wy * iw; s.wz = c->wz * iw;
    s.nx = c->nx * iw; s.ny = c->ny * iw; s.nz = c->nz * iw;
    s.u = c->u * iw; s.v = c->v * iw;
    return s;
}

static void s3d_pushTri(S3dFrame* f, int blended, const S3dTri* t) {
    if (blended) { if (S3D_GROW(f->blended, f->blendedCap, f->blendedN + 1)) f->blended[f->blendedN++] = *t; }
    else if (S3D_GROW(f->opaque, f->opaqueCap, f->opaqueN + 1)) f->opaque[f->opaqueN++] = *t;
}

/* Software: the vertices of a batch in world and clip space. 0 when the memory is gone. */
static int s3d_transformBatch(S3dBatch* batch) {
    const S3dMesh* mesh = batch->mesh;
    const S3dSurface* surf = &batch->s;
    const S3dMat4* vp = &batch->vp;
    s3d_size vertexCount = mesh->posN / 3, i;
    batch->cvN = 0;
    if (!S3D_GROW(batch->cv, batch->cvCap, vertexCount + 1)) return 0;
    batch->cvN = vertexCount;
    for (i = 0; i < vertexCount; i++) {
        S3dVec3 wp = s3d_transformPoint(&batch->model, s3d_meshPos(mesh, i));
        S3dVec3 wn = s3d_normalize(s3d_transformDir(&batch->normalM, s3d_meshNrm(mesh, i)));
        S3dClipVertex* c = &batch->cv[i];
        float u, v;
        s3d_meshUv(mesh, i, &u, &v);
        c->wx = wp.x; c->wy = wp.y; c->wz = wp.z; c->nx = wn.x; c->ny = wn.y; c->nz = wn.z;
        c->u = surf->tm[0] * u + surf->tm[1] * v + surf->tm[2];   /* the texture mapping is applied to the corners: the rest is interpolated */
        c->v = surf->tm[3] * u + surf->tm[4] * v + surf->tm[5];
        c->x = S3D_AT(*vp, 0, 0) * wp.x + S3D_AT(*vp, 0, 1) * wp.y + S3D_AT(*vp, 0, 2) * wp.z + S3D_AT(*vp, 0, 3);
        c->y = S3D_AT(*vp, 1, 0) * wp.x + S3D_AT(*vp, 1, 1) * wp.y + S3D_AT(*vp, 1, 2) * wp.z + S3D_AT(*vp, 1, 3);
        c->z = S3D_AT(*vp, 2, 0) * wp.x + S3D_AT(*vp, 2, 1) * wp.y + S3D_AT(*vp, 2, 2) * wp.z + S3D_AT(*vp, 2, 3);
        c->w = S3D_AT(*vp, 3, 0) * wp.x + S3D_AT(*vp, 3, 1) * wp.y + S3D_AT(*vp, 3, 2) * wp.z + S3D_AT(*vp, 3, 3);
    }
    return 1;
}

/* Software: the triangles of a (transformed) batch, cut against the near plane and set up on the screen. */
static void s3d_setupBatch(S3dFrame* f, int batchIndex) {
    const S3dBatch* batch = &f->batches[batchIndex];
    const S3dMesh* mesh = batch->mesh;
    const S3dSurface* surf = &batch->s;
    const S3dClipVertex* cv = batch->cv;
    s3d_size vertexCount = batch->cvN, t;
    S3dVec3 campos = batch->cam.pos;
    /* a blended object that is drawn from both sides shows its far side first */
    int sweeps = (batch->blended && surf->twoSided) ? 2 : 1, sweep;
    for (sweep = 0; sweep < sweeps; sweep++)
        for (t = 0; t + 2 < mesh->idxN; t += 3) {
            uint32_t i0 = s3d_meshIdx(mesh, t), i1 = s3d_meshIdx(mesh, t + 1), i2 = s3d_meshIdx(mesh, t + 2);
            S3dVec3 wa, wb, wc, fn, tangent, bitangent;
            int facing, count = 0, e, i, constNormal;
            S3dClipVertex poly[4];
            const S3dClipVertex* in[3];
            S3dScreenVertex sv[4];
            if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount) continue;
            wa = s3d_v3(cv[i0].wx, cv[i0].wy, cv[i0].wz); wb = s3d_v3(cv[i1].wx, cv[i1].wy, cv[i1].wz); wc = s3d_v3(cv[i2].wx, cv[i2].wy, cv[i2].wz);
            fn = s3d_normalize(s3d_cross(s3d_sub(wb, wa), s3d_sub(wc, wa)));
            facing = s3d_dot(fn, s3d_sub(campos, wa)) > 0;
            if (sweeps == 2) { if ((sweep == 0) == facing) continue; }          /* far side first, then the near side */
            else if (!surf->twoSided && !facing) continue;                      /* back face */
            /* clip against the near plane (z >= -w), the other planes are handled by the rows and columns */
            in[0] = &cv[i0]; in[1] = &cv[i1]; in[2] = &cv[i2];
            for (e = 0; e < 3; e++) {
                const S3dClipVertex* cur = in[e];
                const S3dClipVertex* nxt = in[(e + 1) % 3];
                float dc = cur->z + cur->w, dn = nxt->z + nxt->w;
                int curIn = dc >= 0, nxtIn = dn >= 0;
                if (curIn) poly[count++] = *cur;
                if (curIn != nxtIn) poly[count++] = s3d_lerpVertex(cur, nxt, dc / (dc - dn));
            }
            if (count < 3) continue;
            for (i = 0; i < count; i++) sv[i] = s3d_toScreen(&poly[i], f->w, f->h);
            /* a face whose three vertex normals agree (a plane, the side of a box) has one normal */
            constNormal = s3d_dot(s3d_v3(cv[i0].nx, cv[i0].ny, cv[i0].nz), s3d_v3(cv[i1].nx, cv[i1].ny, cv[i1].nz)) > 0.99999f &&
                          s3d_dot(s3d_v3(cv[i0].nx, cv[i0].ny, cv[i0].nz), s3d_v3(cv[i2].nx, cv[i2].ny, cv[i2].nz)) > 0.99999f;
            tangent = s3d_v3(1, 0, 0); bitangent = s3d_v3(0, 0, 1);
            if (batch->bumpWanted) {
                /* the directions of increasing u and v on the face */
                S3dVec3 e1 = s3d_sub(wb, wa), e2 = s3d_sub(wc, wa);
                float du1 = cv[i1].u - cv[i0].u, dv1 = cv[i1].v - cv[i0].v, du2 = cv[i2].u - cv[i0].u, dv2 = cv[i2].v - cv[i0].v;
                float det = du1 * dv2 - du2 * dv1;
                if (fabsf(det) > 1e-12f) {
                    float r = 1.0f / det;
                    tangent = s3d_normalize(s3d_scale(s3d_sub(s3d_scale(e1, dv2), s3d_scale(e2, dv1)), r));
                    bitangent = s3d_normalize(s3d_scale(s3d_sub(s3d_scale(e2, du1), s3d_scale(e1, du2)), r));
                }
            }
            for (i = 1; i + 1 < count; i++) {
                S3dTri tri;
                tri.batch = batchIndex;
                if (!s3d_setupTri(&tri, &sv[0], &sv[i], &sv[i + 1], f->h)) continue;
                tri.fn = fn;
                tri.constNormal = constNormal;
                tri.bump = batch->bumpWanted;
                tri.tangent = tangent; tri.bitangent = bitangent;
                s3d_pushTri(f, batch->blended, &tri);
            }
        }
}

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * Threads: fn(arg, 0) .. fn(arg, count - 1) on `workers` threads; each takes the next index that nobody has taken yet (the calls must not depend on each other, so the result does not
 * depend on which thread did what).
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
typedef void (*S3dTask)(void* arg, int index);
typedef struct S3dJobQueue {
    S3dTask fn; void* arg; int count; int next;
#ifdef S3D_THREADS
#ifdef _WIN32
    CRITICAL_SECTION lock;
#else
    pthread_mutex_t lock;
#endif
#endif
} S3dJobQueue;

static int s3d_takeIndex(S3dJobQueue* q) {
    int i;
#ifdef S3D_THREADS
#ifdef _WIN32
    EnterCriticalSection(&q->lock); i = q->next++; LeaveCriticalSection(&q->lock);
#else
    pthread_mutex_lock(&q->lock); i = q->next++; pthread_mutex_unlock(&q->lock);
#endif
#else
    i = q->next++;
#endif
    return i;
}
static void s3d_runQueue(S3dJobQueue* q) { int i; while ((i = s3d_takeIndex(q)) < q->count) q->fn(q->arg, i); }

#ifdef S3D_THREADS
#ifdef _WIN32
static DWORD WINAPI s3d_threadMain(LPVOID p) { s3d_runQueue((S3dJobQueue*)p); return 0; }
#else
static void* s3d_threadMain(void* p) { s3d_runQueue((S3dJobQueue*)p); return NULL; }
#endif
#endif

static int s3d_hardwareThreads(void) {
#ifdef S3D_THREADS
#ifdef _WIN32
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    return (int)si.dwNumberOfProcessors;
#else
    long n = sysconf(_SC_NPROCESSORS_ONLN);
    return n > 0 ? (int)n : 1;
#endif
#else
    return 1;
#endif
}

static int s3d_workerCount(const S3dFlushSettings* st, s3d_size items) {
#ifdef S3D_THREADS
    int n = st->threads;
    if (n <= 0) { n = s3d_hardwareThreads(); if (n <= 0) n = 1; if (n > 8) n = 8; }
    if ((s3d_size)n > items) n = (int)items;
    return n < 1 ? 1 : n;
#else
    (void)st; (void)items;
    return 1;
#endif
}

#define S3D_MAX_WORKERS 64
static void s3d_parallelFor(int count, int workers, S3dTask fn, void* arg) {
    S3dJobQueue q;
    int i;
    q.fn = fn; q.arg = arg; q.count = count; q.next = 0;
    if (workers > S3D_MAX_WORKERS) workers = S3D_MAX_WORKERS;
    if (workers > count) workers = count;
#ifdef S3D_THREADS
    if (workers > 1) {
#ifdef _WIN32
        HANDLE th[S3D_MAX_WORKERS];
        InitializeCriticalSection(&q.lock);
        for (i = 1; i < workers; i++) th[i] = CreateThread(NULL, 0, s3d_threadMain, &q, 0, NULL);
        s3d_runQueue(&q);   /* (a thread that could not be started leaves its share to the others) */
        for (i = 1; i < workers; i++) if (th[i] != NULL) { WaitForSingleObject(th[i], INFINITE); CloseHandle(th[i]); }
        DeleteCriticalSection(&q.lock);
#else
        pthread_t th[S3D_MAX_WORKERS];
        int started[S3D_MAX_WORKERS];
        pthread_mutex_init(&q.lock, NULL);
        for (i = 1; i < workers; i++) started[i] = pthread_create(&th[i], NULL, s3d_threadMain, &q) == 0;
        s3d_runQueue(&q);
        for (i = 1; i < workers; i++) if (started[i]) pthread_join(th[i], NULL);
        pthread_mutex_destroy(&q.lock);
#endif
        return;
    }
    (void)i;
#else
    (void)i; (void)workers;
#endif
    for (q.next = 0; q.next < count; q.next++) fn(arg, q.next);
}

#endif /* FIRE_S3D_LEAN_ONLY */

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * Shadow maps: the depth of what casts a shadow, seen from the light (an orthographic view for a directional light, six views for a point light)
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
static S3dMat4 s3d_lookAtMatrix(S3dVec3 eye, S3dVec3 forward, S3dVec3 upHint) {
    S3dVec3 z = s3d_normalize(s3d_scale(forward, -1.0f));
    S3dVec3 x = s3d_cross(upHint, z), y;
    S3dMat4 r = s3d_identity();
    if (s3d_length(x) < 1e-4f) x = s3d_cross(s3d_v3(1, 0, 0), z);
    x = s3d_normalize(x);
    y = s3d_cross(z, x);
    S3D_AT(r, 0, 0) = x.x; S3D_AT(r, 0, 1) = x.y; S3D_AT(r, 0, 2) = x.z; S3D_AT(r, 0, 3) = -s3d_dot(x, eye);
    S3D_AT(r, 1, 0) = y.x; S3D_AT(r, 1, 1) = y.y; S3D_AT(r, 1, 2) = y.z; S3D_AT(r, 1, 3) = -s3d_dot(y, eye);
    S3D_AT(r, 2, 0) = z.x; S3D_AT(r, 2, 1) = z.y; S3D_AT(r, 2, 2) = z.z; S3D_AT(r, 2, 3) = -s3d_dot(z, eye);
    return r;
}

#ifndef FIRE_S3D_LEAN_ONLY   /* (the full renderer: shadow maps, lights, flush) */
typedef struct S3dDepthVertex { float sx, sy, d; } S3dDepthVertex;

static void s3d_rasterDepth(S3dShadowMap* m, const S3dDepthVertex* a, const S3dDepthVertex* b, const S3dDepthVertex* c) {
    float sx[3], sy[3], det, inv, v0, v1, v2, base, gx, gy;
    int minY, maxY, y, x, e;
    sx[0] = a->sx; sx[1] = b->sx; sx[2] = c->sx; sy[0] = a->sy; sy[1] = b->sy; sy[2] = c->sy;
    det = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
    if (fabsf(det) < 1e-9f) return;
    inv = 1.0f / det;
    /* perspective: 1/d is linear on the screen; orthographic: d is */
    v0 = m->ortho ? a->d : 1.0f / a->d; v1 = m->ortho ? b->d : 1.0f / b->d; v2 = m->ortho ? c->d : 1.0f / c->d;
    s3d_planeOf(sx, sy, v0, v1, v2, inv, &base, &gx, &gy);
    minY = s3d_maxi(0, (int)ceilf(s3d_min3f(sy[0], sy[1], sy[2]) - 0.5f)); maxY = s3d_mini(m->res - 1, (int)ceilf(s3d_max3f(sy[0], sy[1], sy[2]) - 0.5f) - 1);
    for (y = minY; y <= maxY; y++) {
        float py = (float)y + 0.5f, xl = 1e30f, xr = -1e30f, value;
        float* row;
        int crossings = 0, xs, xe;
        for (e = 0; e < 3; e++) {
            int j = (e + 1) % 3;
            float ya = sy[e], yb = sy[j], lo, hi, cx;
            if (ya == yb) continue;
            lo = ya < yb ? ya : yb; hi = ya < yb ? yb : ya;
            if (py < lo || py >= hi) continue;
            cx = sx[e] + (py - ya) * (sx[j] - sx[e]) / (yb - ya);
            if (cx < xl) xl = cx;
            if (cx > xr) xr = cx;
            crossings++;
        }
        if (crossings < 2) continue;
        xs = s3d_maxi(0, (int)ceilf(xl - 0.5f)); xe = s3d_mini(m->res - 1, (int)ceilf(xr - 0.5f) - 1);
        value = base + gx * ((float)xs + 0.5f - sx[0]) + gy * (py - sy[0]);
        row = &m->depth[(s3d_size)y * (s3d_size)m->res];
        for (x = xs; x <= xe; x++, value += gx) {
            float d = m->ortho ? value : (value > 0 ? 1.0f / value : 1e30f);
            if (d < row[x]) row[x] = d;
        }
    }
}

/* Draws the casters (every batch that casts shadows) into the map. The view of the map is set; the triangles are cut against its near plane when it is a perspective one. */
static void s3d_renderShadowMap(S3dShadowMap* m, const S3dFrame* f, float nearPlane) {
    s3d_size n = (s3d_size)m->res * (s3d_size)m->res, i, bi, t;
    S3dVec3* view = NULL;
    s3d_size viewCap = 0;
    for (i = 0; i < n; i++) m->depth[i] = 1e30f;
    for (bi = 0; bi < f->batchN; bi++) {
        const S3dBatch* b = &f->batches[bi];
        const S3dMesh* mesh = b->mesh;
        if (!b->s.castShadows || b->blended || !mesh) continue;   /* transparent objects cast no shadow */
        if (!S3D_GROW(view, viewCap, b->cvN + 1)) break;
        for (i = 0; i < b->cvN; i++) view[i] = s3d_transformPoint(&m->view, s3d_v3(b->cv[i].wx, b->cv[i].wy, b->cv[i].wz));
        for (t = 0; t + 2 < mesh->idxN; t += 3) {
            uint32_t ix[3];
            S3dVec3 poly[4];
            S3dDepthVertex dv[4];
            int count = 0, e, k;
            ix[0] = s3d_meshIdx(mesh, t); ix[1] = s3d_meshIdx(mesh, t + 1); ix[2] = s3d_meshIdx(mesh, t + 2);
            if (ix[0] >= b->cvN || ix[1] >= b->cvN || ix[2] >= b->cvN) continue;
            for (e = 0; e < 3; e++) {
                S3dVec3 cur = view[ix[e]], nxt = view[ix[(e + 1) % 3]];
                float dc, dn;
                int curIn, nxtIn;
                if (m->ortho) { poly[count++] = cur; continue; }
                dc = -cur.z - nearPlane; dn = -nxt.z - nearPlane;
                curIn = dc >= 0; nxtIn = dn >= 0;
                if (curIn) poly[count++] = cur;
                if (curIn != nxtIn) { float kk = dc / (dc - dn); poly[count++] = s3d_add(cur, s3d_scale(s3d_sub(nxt, cur), kk)); }
            }
            if (count < 3) continue;
            for (k = 0; k < count; k++) {
                float d = -poly[k].z, u, v;
                if (m->ortho) { u = poly[k].x * m->sx + m->ox; v = 1.0f - (poly[k].y * m->sy + m->oy); }
                else { float kk = m->f * 0.5f / d; u = poly[k].x * kk + 0.5f; v = 1.0f - (poly[k].y * kk + 0.5f); }
                dv[k].sx = u * (float)m->res; dv[k].sy = v * (float)m->res; dv[k].d = d;
            }
            for (k = 1; k + 1 < count; k++) s3d_rasterDepth(m, &dv[0], &dv[k], &dv[k + 1]);
        }
    }
    S3D_FREE(view);
}

typedef struct S3dShadowJob { S3dShadowMap** maps; const S3dFrame* f; } S3dShadowJob;
static void s3d_shadowTask(void* arg, int i) { S3dShadowJob* j = (S3dShadowJob*)arg; s3d_renderShadowMap(j->maps[i], j->f, 0.05f); }

/* The shadow maps of the lights that cast shadows, around the casters whose bounds are lo..hi (world): one orthographic map for a directional light, six for a point light. Sets their
 * views and sizes (not their depth); the n-th light that casts shadows has the n-th set. Also used by the hardware backend. */
static void s3d_planShadows(S3dContext* ctx, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st, S3dVec3 lo, S3dVec3 hi) {
    int res = s3d_maxi(64, s3d_mini(4096, st->shadowResolution));
    s3d_size li;
    ctx->shadowN = 0;
    for (li = 0; li < lightCount; li++) {
        const S3dLight* l = &lights[li];
        S3dShadowSet* set;
        if (!l->castShadows || l->kind == S3D_LIGHT_AMBIENT) continue;
        if (!S3D_GROW(ctx->shadows, ctx->shadowCap, (s3d_size)ctx->shadowN + 1)) break;
        if ((s3d_size)ctx->shadowN >= ctx->shadowInit) { memset(&ctx->shadows[ctx->shadowN], 0, sizeof(S3dShadowSet)); ctx->shadowInit = (s3d_size)ctx->shadowN + 1; }
        set = &ctx->shadows[ctx->shadowN];
        if (l->kind == S3D_LIGHT_DIRECTIONAL) {
            S3dVec3 forward = s3d_normalize(l->dir);
            S3dVec3 up = fabsf(forward.y) > 0.99f ? s3d_v3(1, 0, 0) : s3d_v3(0, 1, 0);
            S3dMat4 view = s3d_lookAtMatrix(s3d_v3(0, 0, 0), forward, up);
            /* the bounds of the casters in the view of the light */
            S3dVec3 vlo = s3d_v3(1e30f, 1e30f, 1e30f), vhi = s3d_v3(-1e30f, -1e30f, -1e30f);
            S3dShadowMap* m = &set->maps[0];
            float margin, extent;
            int c;
            for (c = 0; c < 8; c++) {
                S3dVec3 corner = s3d_v3((c & 1) ? hi.x : lo.x, (c & 2) ? hi.y : lo.y, (c & 4) ? hi.z : lo.z);
                S3dVec3 q = s3d_transformPoint(&view, corner);
                vlo = s3d_v3(s3d_minf(vlo.x, q.x), s3d_minf(vlo.y, q.y), s3d_minf(vlo.z, q.z));
                vhi = s3d_v3(s3d_maxf(vhi.x, q.x), s3d_maxf(vhi.y, q.y), s3d_maxf(vhi.z, q.z));
            }
            margin = 0.05f * s3d_maxf(vhi.x - vlo.x, vhi.y - vlo.y) + 0.01f;
            vlo.x -= margin; vlo.y -= margin; vhi.x += margin; vhi.y += margin;
            set->count = 1;
            m->res = res; m->ortho = 1; m->view = view;
            /* the map is square: the longer side decides the scale, so that a texel is as wide as it is high */
            extent = s3d_maxf(vhi.x - vlo.x, vhi.y - vlo.y);
            m->sx = m->sy = 1.0f / extent;
            m->ox = -vlo.x * m->sx; m->oy = -vlo.y * m->sy;
            m->f = 1;
        } else {
            static const float dirs[6][3] = {{1, 0, 0}, {-1, 0, 0}, {0, 1, 0}, {0, -1, 0}, {0, 0, 1}, {0, 0, -1}};
            static const float ups[6][3] = {{0, 1, 0}, {0, 1, 0}, {0, 0, 1}, {0, 0, 1}, {0, 1, 0}, {0, 1, 0}};
            int face;
            set->count = 6;
            for (face = 0; face < 6; face++) {
                S3dShadowMap* m = &set->maps[face];
                m->res = s3d_maxi(64, res / 2); m->ortho = 0;
                m->view = s3d_lookAtMatrix(l->pos, s3d_v3(dirs[face][0], dirs[face][1], dirs[face][2]), s3d_v3(ups[face][0], ups[face][1], ups[face][2]));
                m->f = 1.0f / tanf(46.0f * 3.14159265358979f / 180.0f);   /* a little more than 90 degrees: the faces overlap at the seams */
                m->sx = m->sy = 1; m->ox = m->oy = 0;
            }
        }
        ctx->shadowN++;
    }
}

/* Software: plans the shadow maps around the casters (from their transformed vertices) and draws them. */
static void s3d_buildShadows(S3dContext* ctx, const S3dFrame* f, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st) {
    int any = 0, k, i, count = 0;
    S3dVec3 lo = s3d_v3(1e30f, 1e30f, 1e30f), hi = s3d_v3(-1e30f, -1e30f, -1e30f);
    s3d_size bi, vi;
    S3dShadowJob job;
    ctx->shadowN = 0;
    if (!st->shadows) return;
    /* the casters' bounds */
    for (bi = 0; bi < f->batchN; bi++) {
        const S3dBatch* b = &f->batches[bi];
        if (!b->s.castShadows || b->blended) continue;
        for (vi = 0; vi < b->cvN; vi++) {
            const S3dClipVertex* v = &b->cv[vi];
            lo = s3d_v3(s3d_minf(lo.x, v->wx), s3d_minf(lo.y, v->wy), s3d_minf(lo.z, v->wz));
            hi = s3d_v3(s3d_maxf(hi.x, v->wx), s3d_maxf(hi.y, v->wy), s3d_maxf(hi.z, v->wz));
            any = 1;
        }
    }
    if (!any) return;
    s3d_planShadows(ctx, lights, lightCount, st, lo, hi);
    job.maps = (S3dShadowMap**)S3D_MALLOC(sizeof(S3dShadowMap*) * ((s3d_size)ctx->shadowN * 6 + 1));
    if (job.maps == NULL) { ctx->shadowN = 0; return; }
    for (k = 0; k < ctx->shadowN; k++)
        for (i = 0; i < ctx->shadows[k].count; i++) {
            S3dShadowMap* m = &ctx->shadows[k].maps[i];
            if (!S3D_GROW(m->depth, m->depthCap, (s3d_size)m->res * (s3d_size)m->res)) { ctx->shadowN = k; goto draw; }
            job.maps[count++] = m;
        }
draw:
    job.f = f;
    s3d_parallelFor(count, s3d_workerCount(st, (s3d_size)count), s3d_shadowTask, &job);
    S3D_FREE(job.maps);
}

/* The lights as the pixels use them (the ambient ones summed up); the n-th light that casts shadows gets the n-th shadow set, when there is one. */
static void s3d_prepareLights(S3dContext* ctx, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st) {
    int shadowIndex = 0;
    s3d_size i;
    ctx->eye = cam->pos;
    ctx->ambient = s3d_v3(0, 0, 0);
    ctx->lightN = 0;
    for (i = 0; i < lightCount; i++) {
        const S3dLight* l = &lights[i];
        S3dPreparedLight p;
        if (l->kind == S3D_LIGHT_AMBIENT) { ctx->ambient = s3d_add(ctx->ambient, s3d_scale(l->color, l->intensity)); continue; }
        p.kind = l->kind;
        p.color = s3d_scale(l->color, l->intensity);
        p.pos = l->pos;
        p.toLight = s3d_normalize(s3d_scale(l->dir, -1.0f));
        p.invRange = 1.0f / (l->range > 1e-6f ? l->range : 1e-6f);
        p.invCutoff2 = l->kind == S3D_LIGHT_POINT && l->cutoff > 0 ? 1.0f / (l->cutoff * l->cutoff) : 0.0f;
        p.mask = l->mask;
        p.shadow = -1;
        if (l->castShadows && st->shadows && shadowIndex < ctx->shadowN) p.shadow = shadowIndex++;
        if (!S3D_GROW(ctx->lights, ctx->lightCap, ctx->lightN + 1)) continue;
        ctx->lights[ctx->lightN++] = p;
    }
}

/* The rectangle of tiles (inclusive) that the sphere (centre, radius) of a light can reach on the screen; 0 when it reaches no visible pixel (behind the camera, beyond
 * its far plane). The box around the sphere in camera space is cut at the near plane and its corners are projected (x / -z is largest and smallest at the corners), with a
 * pixel to spare for the rounding. */
static int s3d_lightTiles(const S3dMat4* view, float tfx, float tfy, int w, int h, float nearP, float farP, S3dVec3 centre, float radius, int* tx0, int* ty0, int* tx1, int* ty1) {
    S3dVec3 c = s3d_transformPoint(view, centre);
    float zlo, zhi, x0 = 1e30f, y0 = 1e30f, x1 = -1e30f, y1 = -1e30f;
    int k;
    /* (the position of a pixel is interpolated in floats: on a face seen almost edge-on it can be off by a little of its distance - the sphere is made larger by that much) */
    radius += 0.01f * s3d_length(c);
    zlo = c.z - radius; zhi = c.z + radius;
    if (zlo > -nearP || zhi < -farP) return 0;   /* (in front of the camera is z <= -near) */
    if (zhi > -nearP) zhi = -nearP;
    for (k = 0; k < 8; k++) {
        float z = (k & 4) ? zhi : zlo, px = (k & 1) ? c.x + radius : c.x - radius, py = (k & 2) ? c.y + radius : c.y - radius;
        float sx = (px * tfx / -z * 0.5f + 0.5f) * (float)w, sy = (1.0f - (py * tfy / -z * 0.5f + 0.5f)) * (float)h;
        x0 = s3d_minf(x0, sx); x1 = s3d_maxf(x1, sx); y0 = s3d_minf(y0, sy); y1 = s3d_maxf(y1, sy);
    }
    x0 -= 1; y0 -= 1; x1 += 1; y1 += 1;
    if (x1 < 0 || y1 < 0 || x0 >= (float)w || y0 >= (float)h) return 0;
    *tx0 = x0 <= 0 ? 0 : (int)x0 >> S3D_TILE_SHIFT; *ty0 = y0 <= 0 ? 0 : (int)y0 >> S3D_TILE_SHIFT;
    *tx1 = x1 >= (float)(w - 1) ? (w - 1) >> S3D_TILE_SHIFT : (int)x1 >> S3D_TILE_SHIFT;
    *ty1 = y1 >= (float)(h - 1) ? (h - 1) >> S3D_TILE_SHIFT : (int)y1 >> S3D_TILE_SHIFT;
    return 1;
}

/* The lights of each tile of the picture (S3dContext.tileLights), when some point light has an end (Light.Cutoff): every light without an end is in every tile, a
 * point light with one only in the tiles its sphere reaches. Without such a light (or culling switched off, or no memory) every pixel looks at every light. */
static void s3d_cullLights(S3dContext* ctx, const S3dCamera* cam, int w, int h, const S3dFlushSettings* st) {
    S3dMat4 view;
    float tf, tfx, tfy;
    s3d_size li;
    int tiles, t, pass, any = 0, tx0, ty0, tx1, ty1, x, y;
    ctx->culled = 0;
    if (st->noLightCulling || w <= 0 || h <= 0) return;
    for (li = 0; li < ctx->lightN; li++) if (ctx->lights[li].invCutoff2 > 0) { any = 1; break; }
    if (!any) return;
    ctx->tilesX = (w + S3D_TILE - 1) >> S3D_TILE_SHIFT; ctx->tilesY = (h + S3D_TILE - 1) >> S3D_TILE_SHIFT;
    tiles = ctx->tilesX * ctx->tilesY;
    if (!S3D_GROW(ctx->tileStart, ctx->tileStartCap, (s3d_size)tiles + 1)) return;
    view = s3d_viewMatrix(cam);
    tf = 1.0f / tanf(cam->fovY * 0.5f);
    tfx = tf / ((float)w / (float)h); tfy = tf;
    /* pass 0 counts the lights of each tile, pass 1 lists them (in the order of the lights) */
    for (pass = 0; pass < 2; pass++) {
        int* at = ctx->tileStart;
        if (pass == 0) for (t = 0; t <= tiles; t++) at[t] = 0;
        for (li = 0; li < ctx->lightN; li++) {
            const S3dPreparedLight* L = &ctx->lights[li];
            if (L->invCutoff2 > 0) {
                if (!s3d_lightTiles(&view, tfx, tfy, w, h, cam->nearPlane, cam->farPlane, L->pos, 1.0f / sqrtf(L->invCutoff2), &tx0, &ty0, &tx1, &ty1)) continue;
            } else { tx0 = 0; ty0 = 0; tx1 = ctx->tilesX - 1; ty1 = ctx->tilesY - 1; }
            for (y = ty0; y <= ty1; y++)
                for (x = tx0; x <= tx1; x++) {
                    t = y * ctx->tilesX + x;
                    if (pass == 0) at[t + 1]++;
                    else ctx->tileLights[at[t]++] = (int)li;
                }
        }
        if (pass == 0) {
            for (t = 0; t < tiles; t++) at[t + 1] += at[t];
            if (!S3D_GROW(ctx->tileLights, ctx->tileLightCap, (s3d_size)at[tiles] + 1)) return;
        } else {
            /* (each start moved on to the start of the next tile: move them back) */
            for (t = tiles; t > 0; t--) at[t] = at[t - 1];
            at[0] = 0;
        }
    }
    ctx->culled = 1;
}

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * Flush: draw the queue
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
/* The blended triangles from far to near, stable (objects at the same distance keep their order): a merge sort of their indices. */
static int s3d_sortBlended(S3dFrame* f) {
    s3d_size n = f->blendedN, width, i;
    int *a, *b;
    if (!S3D_GROW(f->order, f->orderCap, n + 1) || !S3D_GROW(f->orderTmp, f->orderTmpCap, n + 1)) return 0;
    a = f->order; b = f->orderTmp;
    for (i = 0; i < n; i++) a[i] = (int)i;
    for (width = 1; width < n; width *= 2) {
        s3d_size lo;
        for (lo = 0; lo < n; lo += 2 * width) {
            s3d_size mid = lo + width < n ? lo + width : n, hi = lo + 2 * width < n ? lo + 2 * width : n, p = lo, q = mid, k = lo;
            while (p < mid && q < hi) {
                /* the right one goes first only when it is farther: equal keys keep their order */
                if (f->batches[f->blended[a[q]].batch].sortKey > f->batches[f->blended[a[p]].batch].sortKey) b[k++] = a[q++];
                else b[k++] = a[p++];
            }
            while (p < mid) b[k++] = a[p++];
            while (q < hi) b[k++] = a[q++];
        }
        { int* sw = a; a = b; b = sw; }
    }
    if (a != f->order) S3D_COPY(f->order, a, n * sizeof(int));
    return 1;
}

typedef struct S3dBandJob { uint8_t* rgba; S3dFrame* f; const S3dContext* ctx; int band; int passes; const S3dCamera* cam; uint32_t sky; } S3dBandJob;
/* `passes`: 1 the opaque triangles, 2 the blended ones, 3 both */
static void s3d_bandTask(void* arg, int bi) {
    S3dBandJob* j = (S3dBandJob*)arg;
    S3dFrame* f = j->f;
    int y0 = bi * j->band, y1 = s3d_mini(f->h - 1, y0 + j->band - 1);
    s3d_size i;
    if (j->passes & 1) for (i = 0; i < f->opaqueN; i++) { const S3dTri* t = &f->opaque[i]; if (t->maxY >= y0 && t->minY <= y1) s3d_rasterTri(j->rgba, f, j->ctx, t, y0, y1, 0); }
    if (j->passes & 2) for (i = 0; i < f->blendedN; i++) { const S3dTri* t = &f->blended[f->order[i]]; if (t->maxY >= y0 && t->minY <= y1) s3d_rasterTri(j->rgba, f, j->ctx, t, y0, y1, 1); }
}

/* ---- reflections (Object.Reflectivity): screen space, after the opaque triangles and before the blended ones ----
 * For a pixel of a surface that reflects and faces up, the ray from the eye is mirrored at its normal and followed through the depth buffer (in camera space, in up to
 * S3D_REFLECT_STEPS steps along its way on the screen, then halved a few times where it went behind what was drawn); what it meets there - in the picture as it was before
 * the reflections - is mixed in by the reflectivity times a fresnel (a grazing view sees the whole reflectivity, a view straight down a fifth). A ray that leaves the
 * picture, meets nothing or the background shows the sky colour (the background, or the fog); near the edges of the picture the found colour fades into it. */
#define S3D_REFLECT_STEPS 16
#define S3D_REFLECT_REFINE 5
#define S3D_REFLECT_DISTANCE 80.0f

/* The skyline of the picture: for each column the first row where something is drawn (h: none), and for each level k the smallest of the columns x .. x + 2^k - 1, so that
 * the smallest of any columns a .. b is two looks. A ray that goes up and is above the skyline of all the columns it can still reach meets nothing more. 0: no memory. */
static int s3d_buildSkyline(S3dFrame* f) {
    int w = f->w, h = f->h, x, y, k, left = w, levels = 1;
    int* top;
    while ((1 << levels) <= w) levels++;
    if (!S3D_GROW(f->skyline, f->skylineCap, (s3d_size)w * (s3d_size)levels)) return 0;
    top = f->skyline;
    for (x = 0; x < w; x++) top[x] = h;
    for (y = 0; y < h && left > 0; y++) {
        const float* row = f->depth + (s3d_size)y * (s3d_size)w;
        for (x = 0; x < w; x++) if (top[x] == h && row[x] < 1e29f) { top[x] = y; left--; }
    }
    for (k = 1; k < levels; k++) {
        int* lo = f->skyline + (s3d_size)(k - 1) * (s3d_size)w, *hi = f->skyline + (s3d_size)k * (s3d_size)w, half = 1 << (k - 1);
        for (x = 0; x < w; x++) hi[x] = x + half < w ? s3d_mini(lo[x], lo[x + half]) : lo[x];
    }
    f->skyLevels = levels;
    return 1;
}
static inline int s3d_skylineMin(const S3dFrame* f, int a, int b) {
    int k = 0;
    if (a > b) { int t = a; a = b; b = t; }
    while ((2 << k) <= b - a + 1) k++;
    return s3d_mini(f->skyline[(s3d_size)k * (s3d_size)f->w + (s3d_size)a], f->skyline[(s3d_size)k * (s3d_size)f->w + (s3d_size)(b - (1 << k) + 1)]);
}

/* The distance (in front of the camera) of what is drawn at a pixel: 0 nothing there. */
static inline float s3d_depthDistance(const S3dFrame* f, s3d_size i, float zA, float zB, float zC) { float z = f->depth[i]; return z >= 1e29f ? 0 : zC / (z * zA + zB); }

static void s3d_reflectTask(void* arg, int bi) {
    S3dBandJob* j = (S3dBandJob*)arg;
    S3dFrame* f = j->f;
    const S3dCamera* cam = j->cam;
    int y0 = bi * j->band, y1 = s3d_mini(f->h - 1, y0 + j->band - 1), x, y, w = f->w, h = f->h;
    float nearP = cam->nearPlane, farP = cam->farPlane, zA = nearP - farP, zB = farP + nearP, zC = 2 * farP * nearP, kA = zA / zC, kB = zB / zC;   /* 1 / distance = z * kA + kB */
    float tf = 1.0f / tanf(cam->fovY * 0.5f), tfx = tf / ((float)w / (float)h), tfy = tf;
    float sky[3], ptx = 2.0f / ((float)w * tfx), pty = 2.0f / ((float)h * tfy), hw = 0.5f * (float)w * tfx, hh = 0.5f * (float)h * tfy;
    S3dMat4 view = s3d_viewMatrix(cam);
    int8_t lastN[2];
    int haveN = 0;
    S3dVec3 n = s3d_v3(0, 1, 0), nv = s3d_v3(0, 1, 0);
    lastN[0] = 0; lastN[1] = 0;
    sky[0] = (float)(j->sky & 255); sky[1] = (float)((j->sky >> 8) & 255); sky[2] = (float)((j->sky >> 16) & 255);
    for (y = y0; y <= y1; y++)
        for (x = 0; x < w; x++) {
            s3d_size di = (s3d_size)y * (s3d_size)w + (s3d_size)x;
            float k = f->reflect[di], d, vl, vn, cosv, fres, amount, len, tEnd, tOut, dt, behind = -1, sxA, syA, sxB, syB, izA, izB, dxs, dys, sPrev = 0, hit = -1, fade = 1, col[3], iq;
            S3dVec3 P, R, Q;
            int steps, s, found = 0, xEnd;
            uint8_t* px;
            const int8_t* on;
            if (k <= 0) continue;
            on = f->reflectN + di * 2;
            if (!haveN || on[0] != lastN[0] || on[1] != lastN[1]) {   /* (a flat surface: the same normal as the pixel before) */
                n = s3d_octDecode(on[0], on[1]);
                nv = s3d_normalize(s3d_transformDir(&view, n));
                lastN[0] = on[0]; lastN[1] = on[1]; haveN = 1;
            }
            if (n.y < 0.5f) continue;   /* (only surfaces that face up: more than 30 degrees) */
            d = s3d_depthDistance(f, di, zA, zB, zC);
            if (d <= 0) continue;
            /* the point in camera space (at the middle of its pixel), the view ray and its mirror image */
            P = s3d_v3(((float)x + 0.5f - 0.5f * (float)w) * ptx * d, (0.5f * (float)h - (float)y - 0.5f) * pty * d, -d);
            vl = s3d_length(P);
            vn = s3d_dot(P, nv) / vl;
            cosv = -vn;
            if (cosv <= 0) continue;
            R = s3d_sub(s3d_scale(P, 1.0f / vl), s3d_scale(nv, 2 * vn));
            { float c = 1 - cosv, c2 = c * c; fres = 0.2f + 0.8f * c2 * c2 * c; }
            amount = k * fres;
            /* the end of the ray: S3D_REFLECT_DISTANCE on, not behind the near plane */
            tEnd = S3D_REFLECT_DISTANCE;
            if (R.z > 1e-6f) { float t = (-nearP * 1.01f - P.z) / R.z; if (t < tEnd) tEnd = t; }
            if (tEnd <= 0) continue;
            Q = s3d_add(P, s3d_scale(R, tEnd));
            iq = 1.0f / -Q.z;
            sxA = (float)x + 0.5f; syA = (float)y + 0.5f;
            sxB = Q.x * iq * hw + 0.5f * (float)w; syB = 0.5f * (float)h - Q.y * iq * hh;
            izA = 1.0f / d; izB = iq;   /* 1 / distance goes evenly over the screen */
            /* (the start a little off the surface: it does not find itself) */
            izA *= 1.0f - 0.002f * cosv;
            dxs = sxB - sxA; dys = syB - syA;
            /* only the part on the screen is followed: where the ray leaves it, the steps end (the shorter, the finer they are) */
            tOut = 1;
            if (dxs > 1e-6f) tOut = s3d_minf(tOut, ((float)w - sxA) / dxs); else if (dxs < -1e-6f) tOut = s3d_minf(tOut, -sxA / dxs);
            if (dys > 1e-6f) tOut = s3d_minf(tOut, ((float)h - syA) / dys); else if (dys < -1e-6f) tOut = s3d_minf(tOut, -syA / dys);
            len = sqrtf(dxs * dxs + dys * dys) * tOut;
            steps = (int)(len / 3.0f) + 1;
            if (steps > S3D_REFLECT_STEPS) steps = S3D_REFLECT_STEPS;
            /* (compared as 1 / distance, which goes evenly along the steps and from the depth buffer: no division a step) */
            dt = tOut / (float)steps;
            xEnd = s3d_maxi(0, s3d_mini(w - 1, (int)(sxA + dxs * tOut)));
            for (s = 1; s <= steps && !found; s++) {
                float t = (float)s * dt, sx = sxA + dxs * t, sy = syA + dys * t, iray, iscene, z;
                int ix = (int)sx, iy = (int)sy;
                if (sx < 0 || sy < 0 || ix >= w || iy >= h) break;
                if (dys < 0 && (s & 3) == 1 && iy < s3d_skylineMin(f, ix, xEnd)) break;   /* above everything it can still reach: only sky from here on */
                z = f->depth[(s3d_size)iy * (s3d_size)w + (s3d_size)ix];
                iray = izA + (izB - izA) * t;
                if (z < 1e29f && iray <= (iscene = z * kA + kB)) {
                    float ray = 1.0f / iray, scene = 1.0f / iscene, prevRay = 1.0f / (izA + (izB - izA) * sPrev);
                    if (ray - scene <= s3d_maxf(ray - prevRay, 0.0f) + 0.05f + 0.02f * scene) {
                        /* behind what is drawn there: halve the last step a few times */
                        float lo = sPrev, hi = t;
                        int r;
                        for (r = 0; r < S3D_REFLECT_REFINE; r++) {
                            float m = (lo + hi) * 0.5f, mx = sxA + dxs * m, my = syA + dys * m, mz;
                            int jx = (int)mx, jy = (int)my;
                            if (mx < 0 || my < 0 || jx >= w || jy >= h) { lo = m; continue; }
                            mz = f->depth[(s3d_size)jy * (s3d_size)w + (s3d_size)jx];
                            if (mz < 1e29f && izA + (izB - izA) * m <= mz * kA + kB) hi = m; else lo = m;
                        }
                        hit = hi;
                        found = 1;
                    }
                    else if (behind < 0) behind = t;
                }
                sPrev = t;
            }
            /* it only went behind something thicker than the step (what is hidden behind it is not on the screen): that thing, rather than a hole */
            if (!found && behind >= 0) { hit = behind; found = 1; }
            px = j->rgba + di * 4;
            col[0] = sky[0]; col[1] = sky[1]; col[2] = sky[2];
            if (found) {
                float hx = sxA + dxs * hit, hy = syA + dys * hit, ex, ey;
                int ix = s3d_maxi(0, s3d_mini(w - 1, (int)floorf(hx))), iy = s3d_maxi(0, s3d_mini(h - 1, (int)floorf(hy)));
                const uint8_t* q = f->reflectSrc + ((s3d_size)iy * (s3d_size)w + (s3d_size)ix) * 4;
                /* near the edges of the picture the found colour goes over into the sky (what lies beyond the edge is not known) */
                ex = s3d_minf(hx, (float)w - hx) / (0.08f * (float)w); ey = s3d_minf(hy, (float)h - hy) / (0.08f * (float)h);
                fade = s3d_maxf(0.0f, s3d_minf(1.0f, s3d_minf(ex, ey)));
                col[0] = sky[0] + ((float)q[0] - sky[0]) * fade; col[1] = sky[1] + ((float)q[1] - sky[1]) * fade; col[2] = sky[2] + ((float)q[2] - sky[2]) * fade;
            }
            px[0] = s3d_toByte(((float)px[0] + (col[0] - (float)px[0]) * amount) / 255.0f);
            px[1] = s3d_toByte(((float)px[1] + (col[1] - (float)px[1]) * amount) / 255.0f);
            px[2] = s3d_toByte(((float)px[2] + (col[2] - (float)px[2]) * amount) / 255.0f);
        }
}

/* Software: the fog over rows y0..y1 of the picture as it is now (the opaque objects; what no object covers is all fog) - s3d_fxApply's fog, done before the transparent objects. */
static void s3d_fogTask(void* arg, int bi) {
    S3dBandJob* j = (S3dBandJob*)arg;
    S3dFrame* f = j->f;
    int y0 = bi * j->band, y1 = s3d_mini(f->h - 1, y0 + j->band - 1), x, y, q;
    float fc[3];
    fc[0] = f->fogColor[0] * 255.0f; fc[1] = f->fogColor[1] * 255.0f; fc[2] = f->fogColor[2] * 255.0f;
    for (y = y0; y <= y1; y++)
        for (x = 0; x < f->w; x++) {
            s3d_size i = (s3d_size)y * (s3d_size)f->w + (s3d_size)x;
            float k = s3d_frameFog(f, f->depth[i]);
            uint8_t* p = j->rgba + i * 4;
            if (k <= 0) continue;
            for (q = 0; q < 3; q++) {
                float c = k >= 1 ? fc[q] : (float)p[q] + (fc[q] - (float)p[q]) * k;
                p[q] = (uint8_t)(c <= 0 ? 0 : c >= 255 ? 255 : (int)(c + 0.5f));
            }
        }
}

/* Software: draws the queue into `rgba` (the camera's picture, already cleared). Opaque objects first (in the order they were queued), then the transparent ones from the far to the near.
 * The rows are shared between threads in bands of 16; a pixel is only ever touched by the thread of its band, so the result does not depend on the number of threads. */
static void s3d_flushSoftware(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st) {
    s3d_size bi;
    S3dBandJob job;
    int bands, workers;
    f->opaqueN = 0; f->blendedN = 0;
    for (bi = 0; bi < f->batchN; bi++) {
        if (!s3d_transformBatch(&f->batches[bi])) continue;
        s3d_setupBatch(f, (int)bi);
    }
    if (f->opaqueN == 0 && f->blendedN == 0) return;
    s3d_buildShadows(&f->ctx, f, lights, lightCount, st);
    s3d_prepareLights(&f->ctx, cam, lights, lightCount, st);
    s3d_cullLights(&f->ctx, cam, f->w, f->h, st);
    if (!s3d_sortBlended(f)) f->blendedN = 0;
    job.rgba = rgba; job.f = f; job.ctx = &f->ctx; job.band = 16; job.passes = 3; job.cam = cam; job.sky = st->sky;
    bands = (f->h + job.band - 1) / job.band;
    workers = (s3d_size)f->w * (s3d_size)f->h < 20000 ? 1 : s3d_workerCount(st, (s3d_size)bands);
    /* reflections: only when an opaque surface of the picture reflects (else nothing of them is done) */
    f->reflectOn = 0;
    for (bi = 0; bi < f->batchN; bi++) if (f->batches[bi].s.reflect > 0 && !f->batches[bi].blended) { f->reflectOn = 1; break; }
    if (f->reflectOn) {
        s3d_size n = (s3d_size)f->w * (s3d_size)f->h;
        if (!S3D_GROW(f->reflect, f->reflectCap, n) || !S3D_GROW(f->reflectN, f->reflectNCap, n * 2) || !S3D_GROW(f->reflectSrc, f->reflectSrcCap, n * 4)) f->reflectOn = 0;
        else S3D_ZERO(f->reflect, n * sizeof(float));
    }
    /* fog with transparent objects: over the opaque ones first, then each transparent one by its own distance (else s3d_fxApply does it afterwards, by the depth of the picture) */
    f->fogOn = st->fogEnd > st->fogStart && f->blendedN > 0;
    if (f->fogOn) {
        f->fogStart = st->fogStart; f->fogEnd = st->fogEnd;
        f->fogZA = cam->nearPlane - cam->farPlane; f->fogZB = cam->farPlane + cam->nearPlane; f->fogZC = 2 * cam->farPlane * cam->nearPlane;
        f->fogColor[0] = (float)(st->fogColor & 255) / 255.0f; f->fogColor[1] = (float)((st->fogColor >> 8) & 255) / 255.0f; f->fogColor[2] = (float)((st->fogColor >> 16) & 255) / 255.0f;
    }
    if (!f->reflectOn && !f->fogOn) { s3d_parallelFor(bands, workers, s3d_bandTask, &job); return; }
    job.passes = 1;
    s3d_parallelFor(bands, workers, s3d_bandTask, &job);
    if (f->reflectOn) {
        S3D_COPY(f->reflectSrc, rgba, (s3d_size)f->w * (s3d_size)f->h * 4);
        if (s3d_buildSkyline(f)) s3d_parallelFor(bands, workers, s3d_reflectTask, &job);
    }
    if (f->fogOn) { s3d_parallelFor(bands, workers, s3d_fogTask, &job); f->fogDone = 1; }
    job.passes = 2;
    s3d_parallelFor(bands, workers, s3d_bandTask, &job);
    f->reflectOn = 0;
    f->fogOn = 0;
}

/* The hardware backend (s3d_gpu_vk.h): 1 when it drew the queue into `rgba`, 2 when it drew it for the windows of the camera only (DirectDrawing), 0 when it cannot (no Vulkan,
 * no device, an error): then the software renderer draws. */
static int s3d_gpu_flush(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st);

/* Draws everything that was queued with `s3d_queueMesh` into `rgba` (the camera's picture, already cleared with FrameClear) and empties the queue. */
static void s3d_flush(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st) {
    int drawn;
    f->fogDone = 0;
    if (f->w <= 0 || f->h <= 0 || f->batchN == 0) { s3d_frameEmpty(f); f->lastBackend = S3D_BACKEND_SOFTWARE; return; }
    drawn = st->backend == S3D_BACKEND_HARDWARE ? s3d_gpu_flush(rgba, f, cam, lights, lightCount, st) : 0;
    if (drawn) f->lastBackend = drawn == 2 ? S3D_BACKEND_DIRECT : S3D_BACKEND_HARDWARE;
    else { s3d_flushSoftware(rgba, f, cam, lights, lightCount, st); f->lastBackend = S3D_BACKEND_SOFTWARE; }
    s3d_frameEmpty(f);
}

#endif /* FIRE_S3D_LEAN_ONLY */

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * Meshes: the templates (a unit size, centred on the origin) and the Wavefront OBJ reader
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
enum { S3D_TEMPLATE_BOX = 0, S3D_TEMPLATE_SPHERE = 1, S3D_TEMPLATE_CYLINDER = 2, S3D_TEMPLATE_CONE = 3, S3D_TEMPLATE_PLANE = 4, S3D_TEMPLATE_QUAD = 5 };

/* The template meshes: counter clockwise seen from outside. A box is 1 x 1 x 1, a sphere has the diameter 1, a cylinder and a cone are 1 high with the diameter 1, a plane is 1 x 1 in the XZ plane
 * facing up (+Y). `detail` is the number of segments around (sphere, cylinder, cone), at least 3. NULL when the memory is gone. */
static S3dMesh* s3d_makeTemplate(int kind, int detail) {
    S3dMesh* m = s3d_meshNew();
    const float pi = 3.14159265358979f;
    int seg = detail < 3 ? 3 : (detail > 256 ? 256 : detail);
    if (m == NULL) return NULL;
    switch (kind) {
        case S3D_TEMPLATE_BOX: {
            static const float faces[6][4][3] = {
                {{-.5f, -.5f, .5f}, {.5f, -.5f, .5f}, {.5f, .5f, .5f}, {-.5f, .5f, .5f}},     /* +Z (front) */
                {{.5f, -.5f, -.5f}, {-.5f, -.5f, -.5f}, {-.5f, .5f, -.5f}, {.5f, .5f, -.5f}}, /* -Z */
                {{.5f, -.5f, .5f}, {.5f, -.5f, -.5f}, {.5f, .5f, -.5f}, {.5f, .5f, .5f}},     /* +X */
                {{-.5f, -.5f, -.5f}, {-.5f, -.5f, .5f}, {-.5f, .5f, .5f}, {-.5f, .5f, -.5f}}, /* -X */
                {{-.5f, .5f, .5f}, {.5f, .5f, .5f}, {.5f, .5f, -.5f}, {-.5f, .5f, -.5f}},     /* +Y (top) */
                {{-.5f, -.5f, -.5f}, {.5f, -.5f, -.5f}, {.5f, -.5f, .5f}, {-.5f, -.5f, .5f}}, /* -Y (bottom) */
            };
            static const float uv[4][2] = {{0, 1}, {1, 1}, {1, 0}, {0, 0}};
            int f, i;
            for (f = 0; f < 6; f++) {
                uint32_t base = (uint32_t)(m->posN / 3);
                for (i = 0; i < 4; i++) s3d_addVertex(m, faces[f][i][0], faces[f][i][1], faces[f][i][2], uv[i][0], uv[i][1]);
                s3d_addTriangle(m, base, base + 1, base + 2);
                s3d_addTriangle(m, base, base + 2, base + 3);
            }
            break;
        }
        case S3D_TEMPLATE_PLANE:
            s3d_addVertex(m, -.5f, 0, .5f, 0, 1); s3d_addVertex(m, .5f, 0, .5f, 1, 1); s3d_addVertex(m, .5f, 0, -.5f, 1, 0); s3d_addVertex(m, -.5f, 0, -.5f, 0, 0);
            s3d_addTriangle(m, 0, 1, 2); s3d_addTriangle(m, 0, 2, 3);
            break;
        case S3D_TEMPLATE_QUAD:   /* 1 x 1 in the XY plane facing +Z: the shape of a billboard */
            s3d_addVertex(m, -.5f, -.5f, 0, 0, 1); s3d_addVertex(m, .5f, -.5f, 0, 1, 1); s3d_addVertex(m, .5f, .5f, 0, 1, 0); s3d_addVertex(m, -.5f, .5f, 0, 0, 0);
            s3d_addTriangle(m, 0, 1, 2); s3d_addTriangle(m, 0, 2, 3);
            break;
        case S3D_TEMPLATE_SPHERE: {
            int rings = seg / 2 < 2 ? 2 : seg / 2, i, j;
            for (i = 0; i <= rings; i++) {
                float th = pi * (float)i / (float)rings;
                for (j = 0; j <= seg; j++) {
                    float ph = 2 * pi * (float)j / (float)seg;
                    s3d_addVertex(m, 0.5f * sinf(th) * cosf(ph), 0.5f * cosf(th), 0.5f * sinf(th) * sinf(ph), (float)j / (float)seg, (float)i / (float)rings);
                }
            }
            for (i = 0; i < rings; i++)
                for (j = 0; j < seg; j++) {
                    uint32_t a = (uint32_t)(i * (seg + 1) + j), b = a + 1, c = a + (uint32_t)(seg + 1), d = c + 1;
                    if (i != 0) s3d_addTriangle(m, a, b, c);
                    if (i != rings - 1) s3d_addTriangle(m, b, d, c);
                }
            break;
        }
        case S3D_TEMPLATE_CYLINDER:
        case S3D_TEMPLATE_CONE: {
            int cone = kind == S3D_TEMPLATE_CONE, j, cap;
            /* the side: two rings of vertices (the top ring of a cone collapses to the apex, one vertex per segment keeps the normals sane) */
            for (j = 0; j <= seg; j++) {
                float ph = 2 * pi * (float)j / (float)seg, cx = cosf(ph), sz = sinf(ph), u = (float)j / (float)seg;
                s3d_addVertex(m, 0.5f * cx, -0.5f, 0.5f * sz, u, 1);
                if (cone) s3d_addVertex(m, 0, 0.5f, 0, u, 0); else s3d_addVertex(m, 0.5f * cx, 0.5f, 0.5f * sz, u, 0);
            }
            for (j = 0; j < seg; j++) {
                uint32_t a = (uint32_t)(2 * j), b = a + 1, c = a + 2, d = a + 3;   /* a bottom, b top, c next bottom, d next top */
                s3d_addTriangle(m, a, b, c);
                if (!cone) s3d_addTriangle(m, c, b, d);
            }
            /* the caps (the bottom one always, the top one for the cylinder): a fan around a centre vertex */
            for (cap = 0; cap < (cone ? 1 : 2); cap++) {
                float y = cap == 0 ? -0.5f : 0.5f;
                uint32_t centre = (uint32_t)(m->posN / 3);
                s3d_addVertex(m, 0, y, 0, 0.5f, 0.5f);
                for (j = 0; j <= seg; j++) {
                    float ph = 2 * pi * (float)j / (float)seg;
                    s3d_addVertex(m, 0.5f * cosf(ph), y, 0.5f * sinf(ph), 0.5f + 0.5f * cosf(ph), 0.5f + 0.5f * sinf(ph));
                }
                for (j = 0; j < seg; j++) {
                    uint32_t a = centre + 1 + (uint32_t)j, b = a + 1;
                    if (cap == 0) s3d_addTriangle(m, centre, a, b); else s3d_addTriangle(m, centre, b, a);
                }
            }
            break;
        }
        default:
            break;
    }
    if (!s3d_meshFinish(m)) { s3d_meshFree(m); return NULL; }
    return m;
}

/* The vertices of an OBJ by their (v, vt) pair: an open addressing table of indices into the keys (the mesh's vertices). */
typedef struct S3dObjKey { int64_t vi, ti; } S3dObjKey;
typedef struct S3dObjKeys { S3dObjKey* keys; s3d_size n, cap; int64_t* slots; s3d_size slotCount; } S3dObjKeys;
static s3d_size s3d_objHash(int64_t vi, int64_t ti, s3d_size mask) {
    uint64_t h = (uint64_t)vi * 0x9E3779B97F4A7C15ull ^ ((uint64_t)ti + 0x632BE59BD9B4E019ull) * 0xC2B2AE3D27D4EB4Full;
    h ^= h >> 29;
    return (s3d_size)h & mask;
}
static int64_t s3d_objFind(const S3dObjKeys* k, int64_t vi, int64_t ti) {
    s3d_size mask, i;
    if (k->slotCount == 0) return -1;
    mask = k->slotCount - 1;
    for (i = s3d_objHash(vi, ti, mask);; i = (i + 1) & mask) {
        int64_t s = k->slots[i];
        if (s < 0) return -1;
        if (k->keys[s].vi == vi && k->keys[s].ti == ti) return s;
    }
}
static int s3d_objInsert(S3dObjKeys* k, int64_t vi, int64_t ti) {
    s3d_size mask, i;
    if (!S3D_GROW(k->keys, k->cap, k->n + 1)) return 0;
    if ((k->n + 1) * 2 > k->slotCount) {
        s3d_size count = k->slotCount ? k->slotCount * 2 : 64, j;
        int64_t* slots = (int64_t*)S3D_MALLOC(count * sizeof(int64_t));
        if (slots == NULL) return 0;
        for (j = 0; j < count; j++) slots[j] = -1;
        S3D_FREE(k->slots);
        k->slots = slots; k->slotCount = count;
        for (j = 0; j < k->n; j++) {
            s3d_size p = s3d_objHash(k->keys[j].vi, k->keys[j].ti, count - 1);
            while (slots[p] >= 0) {
                if (k->keys[slots[p]].vi == k->keys[j].vi && k->keys[slots[p]].ti == k->keys[j].ti) break;   /* the first one stays (as a search from the start finds it) */
                p = (p + 1) & (count - 1);
            }
            if (slots[p] < 0) slots[p] = (int64_t)j;
        }
    }
    k->keys[k->n].vi = vi; k->keys[k->n].ti = ti;
    mask = k->slotCount - 1;
    for (i = s3d_objHash(vi, ti, mask);; i = (i + 1) & mask) {
        if (k->slots[i] < 0) { k->slots[i] = (int64_t)k->n; break; }
        if (k->keys[k->slots[i]].vi == vi && k->keys[k->slots[i]].ti == ti) break;
    }
    k->n++;
    return 1;
}

typedef struct S3dObjState { S3dMesh* m; float* vp; s3d_size vpN, vpCap; float* vt; s3d_size vtN, vtCap; S3dObjKeys keys; } S3dObjState;

/* The vertex of the mesh for a (v, vt) reference of a face: an earlier one, or a new one; -1 when the reference is not there. (The pair is looked up as written and kept with the index of
 * v counted from the start: as the reader always did.) */
static int64_t s3d_objVertexFor(S3dObjState* o, int64_t vi, int64_t ti) {
    int64_t found = s3d_objFind(&o->keys, vi, ti);
    s3d_size nv = o->vpN / 3;
    float u = 0, v = 0;
    if (found >= 0) return found;
    if (vi < 0) vi += (int64_t)nv + 1;
    if (vi < 1 || (s3d_size)vi > nv) return -1;
    if (ti != 0) {
        s3d_size nt = o->vtN / 2;
        int64_t t = ti < 0 ? ti + (int64_t)nt + 1 : ti;
        if (t >= 1 && (s3d_size)t <= nt) { u = o->vt[(s3d_size)(t - 1) * 2]; v = 1.0f - o->vt[(s3d_size)(t - 1) * 2 + 1]; }
    }
    s3d_addVertex(o->m, o->vp[(s3d_size)(vi - 1) * 3], o->vp[(s3d_size)(vi - 1) * 3 + 1], o->vp[(s3d_size)(vi - 1) * 3 + 2], u, v);
    if (!s3d_objInsert(&o->keys, vi, ti)) { o->m->bad = 1; return -1; }
    return (int64_t)o->keys.n - 1;
}

/* Reads a Wavefront OBJ: `v`, `vt` and `f` (triangles, quads and polygons are fanned; `v`, `v/vt`, `v//vn` and `v/vt/vn` are understood, negative indices count from the end). `vn` is ignored:
 * the normals are computed (smooth). NULL when nothing usable is in the text. */
static S3dMesh* s3d_parseObj(const char* text, s3d_size size) {
    S3dObjState o;
    char* line = NULL;
    s3d_size lineCap = 0, pos = 0;
    int64_t* face = NULL;
    s3d_size faceCap = 0;
    memset(&o, 0, sizeof o);
    o.m = s3d_meshNew();
    if (o.m == NULL) return NULL;
    while (pos < size && !o.m->bad) {
        s3d_size end = pos, len, k;
        const char* p;
        while (end < size && text[end] != '\n') end++;
        len = end - pos;
        if (!S3D_GROW(line, lineCap, len + 1)) { o.m->bad = 1; break; }
        memcpy(line, text + pos, len);
        line[len] = 0;
        pos = end + 1;
        for (k = 0; k < len; k++) if (line[k] == '#') { line[k] = 0; break; }
        p = line;
        while (*p == ' ' || *p == '\t' || *p == '\r') p++;
        if (p[0] == 'v' && (p[1] == ' ' || p[1] == '\t')) {
            float x = 0, y = 0, z = 0;
            if (sscanf(p + 1, "%f %f %f", &x, &y, &z) >= 3) {
                if (!S3D_GROW(o.vp, o.vpCap, o.vpN + 3)) { o.m->bad = 1; break; }
                o.vp[o.vpN++] = x; o.vp[o.vpN++] = y; o.vp[o.vpN++] = z;
            }
        } else if (p[0] == 'v' && p[1] == 't') {
            float u = 0, v = 0;
            if (sscanf(p + 2, "%f %f", &u, &v) >= 2) {
                if (!S3D_GROW(o.vt, o.vtCap, o.vtN + 2)) { o.m->bad = 1; break; }
                o.vt[o.vtN++] = u; o.vt[o.vtN++] = v;
            }
        } else if (p[0] == 'f' && (p[1] == ' ' || p[1] == '\t')) {
            s3d_size faceN = 0, i;
            const char* q = p + 1;
            while (*q) {
                char* e;
                long vi, ti = 0;
                int64_t id;
                while (*q == ' ' || *q == '\t' || *q == '\r') q++;
                if (!*q) break;
                vi = strtol(q, &e, 10);
                if (e == q) break;
                q = e;
                if (*q == '/') {
                    q++;
                    if (*q != '/') { ti = strtol(q, &e, 10); q = e; }
                    if (*q == '/') { q++; (void)strtol(q, &e, 10); q = e; }
                }
                id = s3d_objVertexFor(&o, vi, ti);
                if (id >= 0) {
                    if (!S3D_GROW(face, faceCap, faceN + 1)) { o.m->bad = 1; break; }
                    face[faceN++] = id;
                }
            }
            for (i = 1; i + 1 < faceN; i++) s3d_addTriangle(o.m, (uint32_t)face[0], (uint32_t)face[i], (uint32_t)face[i + 1]);
        }
    }
    S3D_FREE(line); S3D_FREE(face); S3D_FREE(o.vp); S3D_FREE(o.vt); S3D_FREE(o.keys.keys); S3D_FREE(o.keys.slots);
    if (o.m->bad || o.m->idxN == 0 || !s3d_meshFinish(o.m)) { s3d_meshFree(o.m); return NULL; }
    return o.m;
}

/* The yaw and pitch (radians) of a camera at `from` that looks at `to`: rx (pitch) and ry (yaw), with the convention of this renderer (looking along -Z at rx = ry = 0). */
static void s3d_lookAngles(S3dVec3 from, S3dVec3 to, float* rx, float* ry) {
    S3dVec3 d = s3d_sub(to, from);
    float horizontal = sqrtf(d.x * d.x + d.z * d.z);
    *ry = atan2f(-d.x, -d.z);
    *rx = atan2f(d.y, horizontal);
}

#endif /* FIRE_S3D_ENGINE_H */
