/* fire scene3d: physics (scene3d.physics, docs/graphics/physics.md) - simple, small and the same everywhere.
 *
 * Bodies: static (the ground, walls: they never move), dynamic (they fall, bounce, are pushed) and kinematic (moved by the program or by their velocity,
 * they push dynamic bodies and are not pushed). Shapes: a box (axis aligned: it does not turn), a sphere, an upright capsule (figures) - and for static
 * bodies the triangles of the object itself (a mesh, in an xz grid so that only the near ones are tested) or the heights of a terrain (no triangles in
 * memory: those of the cells below a body are made when they are needed).
 * A step: gravity and damping, moving, then the contacts are resolved a few times (pushed apart, the velocity along the contact reflected by the bounce,
 * the sideways velocity slowed by the friction). Bodies do not turn (no angular velocity). A character is an upright capsule that walks (its sideways
 * velocity is set), jumps, climbs steps up to a height and stands on slopes up to an angle.
 * Positions are those of the objects (the bridge copies them in and out); everything is float and deterministic: the same steps give the same
 * positions in the VM and in a native build. */
#ifndef FIRE_S3D_PHYSICS_H
#define FIRE_S3D_PHYSICS_H

#ifndef FIRE_S3D_ENGINE_H
#include "s3d_engine.h"
#endif

enum { S3D_BODY_STATIC = 0, S3D_BODY_DYNAMIC = 1, S3D_BODY_KINEMATIC = 2 };
enum { S3D_SHAPE_MESH = 0, S3D_SHAPE_BOX = 1, S3D_SHAPE_SPHERE = 2, S3D_SHAPE_CAPSULE = 3 };
#define S3D_TOUCH_MAX 8

/* triangles in the world, in an xz grid of cells (each cell lists the triangles whose box reaches into it) */
typedef struct S3dSoup {
    float* v; s3d_size vN;           /* vertices x, y, z */
    uint32_t* t; s3d_size tN;        /* 3 indices a triangle (tN: the number of triangles) */
    S3dVec3 lo, hi;
    int gx, gz; float cx, cz;        /* cells and their size */
    uint32_t* start; uint32_t* list; /* start[cell] .. start[cell + 1]: the triangles of a cell in `list` */
} S3dSoup;

/* the heights of a terrain (see fire_s3d_terrainMesh: local x, z centred, cell size, height step; hard: blocks with walls) and where it stands */
typedef struct S3dHeights { const float* H; int cols, rows, hard; float cell, step, minH; S3dMat4 model, inv; } S3dHeights;

typedef struct S3dBody {
    int kind, shape, trigger, character, twoSided;
    S3dVec3 pos;                     /* the position of the object (copied in and out) */
    S3dVec3 offset, half;            /* the shape: its centre from pos; a box: half sizes; sphere: half.x the radius; capsule: half.x the radius, half.y half the segment */
    S3dVec3 vel, moved;              /* velocity; a kinematic body: its velocity from how the program moved it */
    float invMass, bounce, friction, damping, gravity;
    S3dSoup* soup; int hasHeights; S3dHeights heights;
    float key[12];                   /* where the object stood when its triangles were made */
    float walkX, walkZ, stepHeight, slopeCos, jump; int onGround, wasOnGround;
    int64_t id; int64_t touch[S3D_TOUCH_MAX]; int touchN;
} S3dBody;

/* a shape in the world for a moment: a box lo..hi, or a segment a..b with a radius (a sphere: a == b) */
typedef struct S3dShape { int box; S3dVec3 lo, hi, a, b; float r; } S3dShape;
typedef struct S3dContact { S3dVec3 n; float depth; } S3dContact;   /* n: the way the first shape is pushed out */

static S3dBody* s3d_bodyNew(void) {
    S3dBody* b = (S3dBody*)s3d_calloc(1, sizeof(S3dBody));
    if (b == NULL) return NULL;
    b->invMass = 1; b->bounce = 0.1f; b->friction = 0.5f; b->gravity = 1; b->stepHeight = 0.3f; b->slopeCos = 0.7071f;
    return b;
}
static void s3d_soupFree(S3dSoup* s) { if (s) { S3D_FREE(s->v); S3D_FREE(s->t); S3D_FREE(s->start); S3D_FREE(s->list); S3D_FREE(s); } }
static void s3d_bodyFree(S3dBody* b) { if (b) { s3d_soupFree(b->soup); S3D_FREE(b); } }

static S3dShape s3d_bodyShape(const S3dBody* b, S3dVec3 pos) {
    S3dShape s;
    S3dVec3 c = s3d_add(pos, b->offset);
    memset(&s, 0, sizeof s);
    if (b->shape == S3D_SHAPE_BOX || b->shape == S3D_SHAPE_MESH) { s.box = 1; s.lo = s3d_sub(c, b->half); s.hi = s3d_add(c, b->half); s.a = s.b = c; return s; }
    s.r = b->half.x;
    s.a = s.b = c;
    if (b->shape == S3D_SHAPE_CAPSULE) { s.a.y -= b->half.y; s.b.y += b->half.y; }
    s.lo = s3d_v3(s3d_minf(s.a.x, s.b.x) - s.r, s3d_minf(s.a.y, s.b.y) - s.r, s3d_minf(s.a.z, s.b.z) - s.r);
    s.hi = s3d_v3(s3d_maxf(s.a.x, s.b.x) + s.r, s3d_maxf(s.a.y, s.b.y) + s.r, s3d_maxf(s.a.z, s.b.z) + s.r);
    return s;
}

/* ---- closest points ---- */
static S3dVec3 s3d_onSegment(S3dVec3 a, S3dVec3 b, S3dVec3 p) {
    S3dVec3 d = s3d_sub(b, a);
    float dd = s3d_dot(d, d), t;
    if (dd < 1e-12f) return a;
    t = s3d_dot(s3d_sub(p, a), d) / dd;
    return s3d_add(a, s3d_scale(d, t < 0 ? 0 : t > 1 ? 1 : t));
}
static S3dVec3 s3d_inBox(S3dVec3 p, S3dVec3 lo, S3dVec3 hi) {
    return s3d_v3(p.x < lo.x ? lo.x : p.x > hi.x ? hi.x : p.x, p.y < lo.y ? lo.y : p.y > hi.y ? hi.y : p.y, p.z < lo.z ? lo.z : p.z > hi.z ? hi.z : p.z);
}
static float s3d_clamp01(float t) { return t < 0 ? 0 : t > 1 ? 1 : t; }
/* the closest points of the segments p1..q1 and p2..q2 (Ericson, Real-Time Collision Detection 5.1.9) */
static void s3d_segSeg(S3dVec3 p1, S3dVec3 q1, S3dVec3 p2, S3dVec3 q2, S3dVec3* c1, S3dVec3* c2) {
    S3dVec3 d1 = s3d_sub(q1, p1), d2 = s3d_sub(q2, p2), r = s3d_sub(p1, p2);
    float a = s3d_dot(d1, d1), e = s3d_dot(d2, d2), f = s3d_dot(d2, r), s, t;
    if (a < 1e-12f && e < 1e-12f) { *c1 = p1; *c2 = p2; return; }
    if (a < 1e-12f) { s = 0; t = s3d_clamp01(f / e); }
    else {
        float c = s3d_dot(d1, r);
        if (e < 1e-12f) { t = 0; s = s3d_clamp01(-c / a); }
        else {
            float b = s3d_dot(d1, d2), den = a * e - b * b;
            s = den > 1e-12f ? s3d_clamp01((b * f - c * e) / den) : 0;
            t = (b * s + f) / e;
            if (t < 0) { t = 0; s = s3d_clamp01(-c / a); }
            else if (t > 1) { t = 1; s = s3d_clamp01((b - c) / a); }
        }
    }
    *c1 = s3d_add(p1, s3d_scale(d1, s)); *c2 = s3d_add(p2, s3d_scale(d2, t));
}
/* the closest point of the triangle a, b, c to p (Ericson 5.1.5) */
static S3dVec3 s3d_onTriangle(S3dVec3 p, S3dVec3 a, S3dVec3 b, S3dVec3 c) {
    S3dVec3 ab = s3d_sub(b, a), ac = s3d_sub(c, a), ap = s3d_sub(p, a), bp, cp;
    float d1 = s3d_dot(ab, ap), d2 = s3d_dot(ac, ap), d3, d4, d5, d6, va, vb, vc, v, w, den;
    if (d1 <= 0 && d2 <= 0) return a;
    bp = s3d_sub(p, b); d3 = s3d_dot(ab, bp); d4 = s3d_dot(ac, bp);
    if (d3 >= 0 && d4 <= d3) return b;
    vc = d1 * d4 - d3 * d2;
    if (vc <= 0 && d1 >= 0 && d3 <= 0) return s3d_add(a, s3d_scale(ab, d1 / (d1 - d3)));
    cp = s3d_sub(p, c); d5 = s3d_dot(ab, cp); d6 = s3d_dot(ac, cp);
    if (d6 >= 0 && d5 <= d6) return c;
    vb = d5 * d2 - d1 * d6;
    if (vb <= 0 && d2 >= 0 && d6 <= 0) return s3d_add(a, s3d_scale(ac, d2 / (d2 - d6)));
    va = d3 * d6 - d5 * d4;
    if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return s3d_add(b, s3d_scale(s3d_sub(c, b), (d4 - d3) / ((d4 - d3) + (d5 - d6))));
    den = 1 / (va + vb + vc); v = vb * den; w = vc * den;
    return s3d_add(a, s3d_add(s3d_scale(ab, v), s3d_scale(ac, w)));
}
/* the closest points of the segment p..q and the triangle (on the segment *ps, on the triangle *pt) */
static void s3d_segTriangle(S3dVec3 p, S3dVec3 q, S3dVec3 a, S3dVec3 b, S3dVec3 c, S3dVec3 n, S3dVec3* ps, S3dVec3* pt) {
    S3dVec3 cs, ct, d = s3d_sub(q, p);
    float best, dist, dn = s3d_dot(d, n);
    int k;
    /* the segment through the triangle */
    if (dn * dn > 1e-12f) {
        float t = s3d_dot(s3d_sub(a, p), n) / dn;
        if (t >= 0 && t <= 1) {
            S3dVec3 x = s3d_add(p, s3d_scale(d, t)), on = s3d_onTriangle(x, a, b, c);
            S3dVec3 e = s3d_sub(on, x);
            if (s3d_dot(e, e) < 1e-10f) { *ps = x; *pt = x; return; }
        }
    }
    *ps = p; *pt = s3d_onTriangle(p, a, b, c);
    best = s3d_dot(s3d_sub(*ps, *pt), s3d_sub(*ps, *pt));
    if (s3d_dot(d, d) < 1e-12f) return;
    ct = s3d_onTriangle(q, a, b, c);
    dist = s3d_dot(s3d_sub(q, ct), s3d_sub(q, ct));
    if (dist < best) { best = dist; *ps = q; *pt = ct; }
    for (k = 0; k < 3; k++) {
        S3dVec3 e0 = k == 0 ? a : k == 1 ? b : c, e1 = k == 0 ? b : k == 1 ? c : a;
        s3d_segSeg(p, q, e0, e1, &cs, &ct);
        dist = s3d_dot(s3d_sub(cs, ct), s3d_sub(cs, ct));
        if (dist < best) { best = dist; *ps = cs; *pt = ct; }
    }
}

/* ---- contacts ---- */
/* a box against a box: the axis where they overlap least */
static int s3d_boxBox(const S3dShape* A, const S3dShape* B, S3dContact* c) {
    float ox = s3d_minf(A->hi.x, B->hi.x) - s3d_maxf(A->lo.x, B->lo.x), oy = s3d_minf(A->hi.y, B->hi.y) - s3d_maxf(A->lo.y, B->lo.y), oz = s3d_minf(A->hi.z, B->hi.z) - s3d_maxf(A->lo.z, B->lo.z);
    if (ox <= 0 || oy <= 0 || oz <= 0) return 0;
    if (oy <= ox && oy <= oz) { c->depth = oy; c->n = s3d_v3(0, A->lo.y + A->hi.y >= B->lo.y + B->hi.y ? 1.0f : -1.0f, 0); }
    else if (ox <= oz) { c->depth = ox; c->n = s3d_v3(A->lo.x + A->hi.x >= B->lo.x + B->hi.x ? 1.0f : -1.0f, 0, 0); }
    else { c->depth = oz; c->n = s3d_v3(0, 0, A->lo.z + A->hi.z >= B->lo.z + B->hi.z ? 1.0f : -1.0f); }
    return 1;
}
/* a sphere or capsule against a box (n pushes the round one out) */
static int s3d_roundBox(const S3dShape* R, const S3dShape* B, S3dContact* c) {
    S3dVec3 mid = s3d_scale(s3d_add(B->lo, B->hi), 0.5f), p = s3d_onSegment(R->a, R->b, mid), q, d;
    float dist;
    int k;
    for (k = 0; k < 3; k++) { q = s3d_inBox(p, B->lo, B->hi); p = s3d_onSegment(R->a, R->b, q); }
    q = s3d_inBox(p, B->lo, B->hi);
    d = s3d_sub(p, q);
    dist = s3d_length(d);
    if (dist > R->r) return 0;
    if (dist > 1e-6f) { c->n = s3d_scale(d, 1 / dist); c->depth = R->r - dist; return 1; }
    {   /* the middle of the round one inside the box: out by the nearest face */
        float f[6];
        int best = 0;
        f[0] = p.x - B->lo.x; f[1] = B->hi.x - p.x; f[2] = p.y - B->lo.y; f[3] = B->hi.y - p.y; f[4] = p.z - B->lo.z; f[5] = B->hi.z - p.z;
        for (k = 1; k < 6; k++) if (f[k] < f[best]) best = k;
        c->n = s3d_v3(best == 0 ? -1.0f : best == 1 ? 1.0f : 0, best == 2 ? -1.0f : best == 3 ? 1.0f : 0, best == 4 ? -1.0f : best == 5 ? 1.0f : 0);
        c->depth = f[best] + R->r;
        return 1;
    }
}
static int s3d_roundRound(const S3dShape* A, const S3dShape* B, S3dContact* c) {
    S3dVec3 pa, pb, d;
    float dist, rr = A->r + B->r;
    s3d_segSeg(A->a, A->b, B->a, B->b, &pa, &pb);
    d = s3d_sub(pa, pb);
    dist = s3d_length(d);
    if (dist >= rr) return 0;
    c->n = dist > 1e-6f ? s3d_scale(d, 1 / dist) : s3d_v3(0, 1, 0);
    c->depth = rr - dist;
    return 1;
}
static int s3d_contactShapes(const S3dShape* A, const S3dShape* B, S3dContact* c) {
    if (A->box && B->box) return s3d_boxBox(A, B, c);
    if (!A->box && !B->box) return s3d_roundRound(A, B, c);
    if (!A->box) return s3d_roundBox(A, B, c);
    if (!s3d_roundBox(B, A, c)) return 0;
    c->n = s3d_scale(c->n, -1);
    return 1;
}

/* a shape against a triangle (n: its front, counter clockwise; twoSided: both sides push out) */
static int s3d_contactTriangle(const S3dShape* A, S3dVec3 a, S3dVec3 b, S3dVec3 c, int twoSided, S3dContact* out) {
    S3dVec3 n = s3d_cross(s3d_sub(b, a), s3d_sub(c, a));
    float nl = s3d_length(n);
    if (nl < 1e-12f) return 0;
    n = s3d_scale(n, 1 / nl);
    if (A->box) {   /* separating axes: the box's three, the face, the nine crossings of edges and box axes */
        S3dVec3 ctr = s3d_scale(s3d_add(A->lo, A->hi), 0.5f), e = s3d_scale(s3d_sub(A->hi, A->lo), 0.5f), tri[3], axis, bestAxis;
        float best = 1e30f, bestScore = 1e30f, side;
        int k, j;
        tri[0] = a; tri[1] = b; tri[2] = c;
        side = s3d_dot(s3d_sub(ctr, a), n);
        if (!twoSided && side < -(e.x * fabsf(n.x) + e.y * fabsf(n.y) + e.z * fabsf(n.z))) return 0;   /* wholly behind */
        bestAxis = n;
        for (k = 0; k < 13; k++) {
            float len, tmin, tmax, rb, pc, ov, t;
            if (k < 3) axis = s3d_v3(k == 0 ? 1.0f : 0, k == 1 ? 1.0f : 0, k == 2 ? 1.0f : 0);
            else if (k == 3) axis = n;
            else {
                S3dVec3 edge = s3d_sub(tri[((k - 4) / 3 + 1) % 3], tri[(k - 4) / 3]), ax = s3d_v3((k - 4) % 3 == 0 ? 1.0f : 0, (k - 4) % 3 == 1 ? 1.0f : 0, (k - 4) % 3 == 2 ? 1.0f : 0);
                axis = s3d_cross(edge, ax);
            }
            len = s3d_length(axis);
            if (len < 1e-6f) continue;
            axis = s3d_scale(axis, 1 / len);
            tmin = tmax = s3d_dot(tri[0], axis);
            for (j = 1; j < 3; j++) { t = s3d_dot(tri[j], axis); if (t < tmin) tmin = t; if (t > tmax) tmax = t; }
            rb = e.x * fabsf(axis.x) + e.y * fabsf(axis.y) + e.z * fabsf(axis.z);
            pc = s3d_dot(ctr, axis);
            ov = s3d_minf(tmax - (pc - rb), (pc + rb) - tmin);
            if (ov <= 0) return 0;
            /* (the face is the better way out when it is about as good) */
            if ((k == 3 ? ov * 0.95f : ov) < bestScore) { bestScore = k == 3 ? ov * 0.95f : ov; best = ov; bestAxis = s3d_dot(s3d_sub(ctr, a), axis) < 0 ? s3d_scale(axis, -1) : axis; }
        }
        if (!twoSided && s3d_dot(bestAxis, n) < -0.01f) { bestAxis = n; best = s3d_dot(n, s3d_sub(a, ctr)) + (e.x * fabsf(n.x) + e.y * fabsf(n.y) + e.z * fabsf(n.z)); }
        out->n = bestAxis; out->depth = best;
        return best > 0;
    } else {
        S3dVec3 ps, pt, d;
        float dist, s;
        s3d_segTriangle(A->a, A->b, a, b, c, n, &ps, &pt);
        d = s3d_sub(ps, pt);
        dist = s3d_length(d);
        if (dist >= A->r) return 0;
        s = s3d_dot(s3d_sub(ps, a), n);
        if (!twoSided && dist > 1e-6f && s3d_dot(d, n) < 0) {
            /* behind the face: only when the middle went just through it, inside the face (behind an edge the other faces of the body answer; deeper
             * than the radius it is the other side of a thin wall) */
            if (-s3d_dot(d, n) < 0.99f * dist || -s > A->r) return 0;
            out->n = n; out->depth = A->r - s;
            return 1;
        }
        if (dist > 1e-6f) { out->n = s3d_scale(d, 1 / dist); out->depth = A->r - dist; }
        else { out->n = (twoSided && s < 0) ? s3d_scale(n, -1) : n; out->depth = A->r; }
        return 1;
    }
}

/* ---- the triangles of static bodies ---- */
static int s3d_boxesMeet(S3dVec3 alo, S3dVec3 ahi, S3dVec3 blo, S3dVec3 bhi) {
    return alo.x <= bhi.x && ahi.x >= blo.x && alo.y <= bhi.y && ahi.y >= blo.y && alo.z <= bhi.z && ahi.z >= blo.z;
}
/* `count` vertices (x, y, z) and `idxN` indices (3 a triangle) become the triangles of a soup (an xz grid of about two triangles a cell). NULL: the memory. */
static S3dSoup* s3d_soupNew(const float* v, s3d_size count, const uint32_t* idx, s3d_size idxN) {
    S3dSoup* s = (S3dSoup*)s3d_calloc(1, sizeof(S3dSoup));
    s3d_size i, tn = idxN / 3, cells, total = 0;
    int pass;
    if (s == NULL) return NULL;
    s->v = (float*)S3D_MALLOC(count * 3 * sizeof(float) + 1);
    s->t = (uint32_t*)S3D_MALLOC(tn * 3 * sizeof(uint32_t) + 1);
    if (s->v == NULL || s->t == NULL) { s3d_soupFree(s); return NULL; }
    S3D_COPY(s->v, v, count * 3 * sizeof(float)); s->vN = count;
    S3D_COPY(s->t, idx, tn * 3 * sizeof(uint32_t)); s->tN = tn;
    s->lo = s3d_v3(1e30f, 1e30f, 1e30f); s->hi = s3d_v3(-1e30f, -1e30f, -1e30f);
    for (i = 0; i < count; i++) {
        S3dVec3 p = s3d_v3(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);
        s->lo = s3d_v3(s3d_minf(s->lo.x, p.x), s3d_minf(s->lo.y, p.y), s3d_minf(s->lo.z, p.z));
        s->hi = s3d_v3(s3d_maxf(s->hi.x, p.x), s3d_maxf(s->hi.y, p.y), s3d_maxf(s->hi.z, p.z));
    }
    {
        float side = sqrtf((float)tn * 0.5f);
        int g = side < 1 ? 1 : side > 64 ? 64 : (int)side;
        s->gx = s->gz = g;
        s->cx = s3d_maxf((s->hi.x - s->lo.x) / (float)g, 1e-3f); s->cz = s3d_maxf((s->hi.z - s->lo.z) / (float)g, 1e-3f);
    }
    cells = (s3d_size)s->gx * (s3d_size)s->gz;
    s->start = (uint32_t*)s3d_calloc(cells + 1, sizeof(uint32_t));
    if (s->start == NULL) { s3d_soupFree(s); return NULL; }
    /* two passes: count the triangles of each cell, then list them */
    for (pass = 0; pass < 2; pass++) {
        uint32_t* fill = NULL;
        if (pass == 1) {
            for (i = 0; i < cells; i++) { uint32_t c = s->start[i]; s->start[i] = (uint32_t)total; total += c; }
            s->start[cells] = (uint32_t)total;
            s->list = (uint32_t*)S3D_MALLOC(total * sizeof(uint32_t) + 1);
            fill = (uint32_t*)s3d_calloc(cells + 1, sizeof(uint32_t));
            if (s->list == NULL || fill == NULL) { S3D_FREE(fill); s3d_soupFree(s); return NULL; }
        }
        for (i = 0; i < tn; i++) {
            const float* p0 = &v[idx[i * 3] * 3]; const float* p1 = &v[idx[i * 3 + 1] * 3]; const float* p2 = &v[idx[i * 3 + 2] * 3];
            int x0 = (int)((s3d_min3f(p0[0], p1[0], p2[0]) - s->lo.x) / s->cx), x1 = (int)((s3d_max3f(p0[0], p1[0], p2[0]) - s->lo.x) / s->cx);
            int z0 = (int)((s3d_min3f(p0[2], p1[2], p2[2]) - s->lo.z) / s->cz), z1 = (int)((s3d_max3f(p0[2], p1[2], p2[2]) - s->lo.z) / s->cz), x, z;
            x0 = s3d_maxi(0, s3d_mini(s->gx - 1, x0)); x1 = s3d_maxi(0, s3d_mini(s->gx - 1, x1));
            z0 = s3d_maxi(0, s3d_mini(s->gz - 1, z0)); z1 = s3d_maxi(0, s3d_mini(s->gz - 1, z1));
            for (z = z0; z <= z1; z++) for (x = x0; x <= x1; x++) {
                s3d_size cell = (s3d_size)z * (s3d_size)s->gx + (s3d_size)x;
                if (pass == 0) s->start[cell]++;
                else s->list[s->start[cell] + fill[cell]++] = (uint32_t)i;
            }
        }
        S3D_FREE(fill);
    }
    return s;
}

/* What is done with each triangle near a box: a contact, a ray. Returns 1 to stop. */
typedef int (*S3dTriVisit)(void* ctx, S3dVec3 a, S3dVec3 b, S3dVec3 c, int twoSided);

static void s3d_soupVisit(const S3dSoup* s, S3dVec3 lo, S3dVec3 hi, int twoSided, S3dTriVisit visit, void* ctx) {
    int x0, x1, z0, z1, x, z;
    s3d_size k;
    if (!s3d_boxesMeet(lo, hi, s->lo, s->hi)) return;
    x0 = s3d_maxi(0, (int)((lo.x - s->lo.x) / s->cx)); x1 = s3d_mini(s->gx - 1, (int)((hi.x - s->lo.x) / s->cx));
    z0 = s3d_maxi(0, (int)((lo.z - s->lo.z) / s->cz)); z1 = s3d_mini(s->gz - 1, (int)((hi.z - s->lo.z) / s->cz));
    for (z = z0; z <= z1; z++) for (x = x0; x <= x1; x++) {
        s3d_size cell = (s3d_size)z * (s3d_size)s->gx + (s3d_size)x;
        for (k = s->start[cell]; k < s->start[cell + 1]; k++) {
            uint32_t t = s->list[k];
            const float* p0 = &s->v[s->t[t * 3] * 3]; const float* p1 = &s->v[s->t[t * 3 + 1] * 3]; const float* p2 = &s->v[s->t[t * 3 + 2] * 3];
            S3dVec3 a = s3d_v3(p0[0], p0[1], p0[2]), b = s3d_v3(p1[0], p1[1], p1[2]), c = s3d_v3(p2[0], p2[1], p2[2]);
            S3dVec3 tlo = s3d_v3(s3d_min3f(a.x, b.x, c.x), s3d_min3f(a.y, b.y, c.y), s3d_min3f(a.z, b.z, c.z)), thi = s3d_v3(s3d_max3f(a.x, b.x, c.x), s3d_max3f(a.y, b.y, c.y), s3d_max3f(a.z, b.z, c.z));
            /* (a triangle in several cells comes once per cell: only where its box starts, or where the query starts) */
            int fx = s3d_maxi(x0, s3d_mini(s->gx - 1, s3d_maxi(0, (int)((tlo.x - s->lo.x) / s->cx)))), fz = s3d_maxi(z0, s3d_mini(s->gz - 1, s3d_maxi(0, (int)((tlo.z - s->lo.z) / s->cz))));
            if (fx != x || fz != z) continue;
            if (!s3d_boxesMeet(lo, hi, tlo, thi)) continue;
            if (visit(ctx, a, b, c, twoSided)) return;
        }
    }
}

/* the triangles of a terrain's cells under a box of the world (made from the heights, as fire_s3d_terrainMesh makes them) */
static int s3d_heightsQuad(const S3dHeights* h, const float* p, S3dTriVisit visit, void* ctx) {
    S3dVec3 q[4];
    int k;
    for (k = 0; k < 4; k++) q[k] = s3d_transformPoint(&h->model, s3d_v3(p[k * 3], p[k * 3 + 1], p[k * 3 + 2]));
    if (visit(ctx, q[0], q[1], q[2], 0)) return 1;
    return visit(ctx, q[0], q[2], q[3], 0);
}
static float s3d_heightAt(const S3dHeights* h, int i, int j) { return i < 0 || j < 0 || i >= h->cols || j >= h->rows ? h->minH : h->H[(s3d_size)j * h->cols + i]; }
static void s3d_heightsVisit(const S3dHeights* h, S3dVec3 lo, S3dVec3 hi, S3dTriVisit visit, void* ctx) {
    S3dVec3 llo = s3d_v3(1e30f, 1e30f, 1e30f), lhi = s3d_v3(-1e30f, -1e30f, -1e30f);
    float x0w, z0w, p[12];
    int c, i, j, i0, i1, j0, j1;
    for (c = 0; c < 8; c++) {   /* the box in the terrain's own space */
        S3dVec3 q = s3d_transformPoint(&h->inv, s3d_v3((c & 1) ? hi.x : lo.x, (c & 2) ? hi.y : lo.y, (c & 4) ? hi.z : lo.z));
        llo = s3d_v3(s3d_minf(llo.x, q.x), s3d_minf(llo.y, q.y), s3d_minf(llo.z, q.z));
        lhi = s3d_v3(s3d_maxf(lhi.x, q.x), s3d_maxf(lhi.y, q.y), s3d_maxf(lhi.z, q.z));
    }
    if (!h->hard) {
        x0w = -(float)(h->cols - 1) * h->cell * 0.5f; z0w = -(float)(h->rows - 1) * h->cell * 0.5f;
        i0 = s3d_maxi(0, (int)floorf((llo.x - x0w) / h->cell)); i1 = s3d_mini(h->cols - 2, (int)floorf((lhi.x - x0w) / h->cell));
        j0 = s3d_maxi(0, (int)floorf((llo.z - z0w) / h->cell)); j1 = s3d_mini(h->rows - 2, (int)floorf((lhi.z - z0w) / h->cell));
        for (j = j0; j <= j1; j++) for (i = i0; i <= i1; i++) {
            float xa = x0w + (float)i * h->cell, za = z0w + (float)j * h->cell;
            S3dVec3 a = s3d_v3(xa, s3d_heightAt(h, i, j) * h->step, za), r = s3d_v3(xa + h->cell, s3d_heightAt(h, i + 1, j) * h->step, za);
            S3dVec3 d = s3d_v3(xa, s3d_heightAt(h, i, j + 1) * h->step, za + h->cell), rd = s3d_v3(xa + h->cell, s3d_heightAt(h, i + 1, j + 1) * h->step, za + h->cell);
            a = s3d_transformPoint(&h->model, a); r = s3d_transformPoint(&h->model, r); d = s3d_transformPoint(&h->model, d); rd = s3d_transformPoint(&h->model, rd);
            if (visit(ctx, a, d, r, 0) || visit(ctx, r, d, rd, 0)) return;
        }
        return;
    }
    x0w = -(float)h->cols * h->cell * 0.5f; z0w = -(float)h->rows * h->cell * 0.5f;
    i0 = s3d_maxi(-1, (int)floorf((llo.x - x0w) / h->cell)); i1 = s3d_mini(h->cols, (int)floorf((lhi.x - x0w) / h->cell));
    j0 = s3d_maxi(-1, (int)floorf((llo.z - z0w) / h->cell)); j1 = s3d_mini(h->rows, (int)floorf((lhi.z - z0w) / h->cell));
    for (j = j0; j <= j1; j++) for (i = i0; i <= i1; i++) {
        float xa = x0w + (float)i * h->cell, xb = xa + h->cell, za = z0w + (float)j * h->cell, zb = za + h->cell, hc = s3d_heightAt(h, i, j), hr = s3d_heightAt(h, i + 1, j), hd = s3d_heightAt(h, i, j + 1);
        if (i >= 0 && j >= 0 && i < h->cols && j < h->rows) {   /* the top of the block */
            float y = hc * h->step;
            p[0] = xa; p[1] = y; p[2] = za;  p[3] = xa; p[4] = y; p[5] = zb;  p[6] = xb; p[7] = y; p[8] = zb;  p[9] = xb; p[10] = y; p[11] = za;
            if (s3d_heightsQuad(h, p, visit, ctx)) return;
        }
        if (j >= 0 && j < h->rows && hc != hr) {   /* the wall at x = xb, to the cell on the right */
            float lo2 = s3d_minf(hc, hr) * h->step, hi2 = s3d_maxf(hc, hr) * h->step;
            if (hc > hr) { p[0] = xb; p[1] = lo2; p[2] = zb;  p[3] = xb; p[4] = lo2; p[5] = za;  p[6] = xb; p[7] = hi2; p[8] = za;  p[9] = xb; p[10] = hi2; p[11] = zb; }
            else { p[0] = xb; p[1] = lo2; p[2] = za;  p[3] = xb; p[4] = lo2; p[5] = zb;  p[6] = xb; p[7] = hi2; p[8] = zb;  p[9] = xb; p[10] = hi2; p[11] = za; }
            if (s3d_heightsQuad(h, p, visit, ctx)) return;
        }
        if (i >= 0 && i < h->cols && hc != hd) {   /* the wall at z = zb, to the cell behind */
            float lo2 = s3d_minf(hc, hd) * h->step, hi2 = s3d_maxf(hc, hd) * h->step;
            if (hc > hd) { p[0] = xa; p[1] = lo2; p[2] = zb;  p[3] = xb; p[4] = lo2; p[5] = zb;  p[6] = xb; p[7] = hi2; p[8] = zb;  p[9] = xa; p[10] = hi2; p[11] = zb; }
            else { p[0] = xb; p[1] = lo2; p[2] = zb;  p[3] = xa; p[4] = lo2; p[5] = zb;  p[6] = xa; p[7] = hi2; p[8] = zb;  p[9] = xb; p[10] = hi2; p[11] = zb; }
            if (s3d_heightsQuad(h, p, visit, ctx)) return;
        }
    }
}
static void s3d_staticVisit(const S3dBody* b, S3dVec3 lo, S3dVec3 hi, S3dTriVisit visit, void* ctx) {
    if (b->hasHeights) s3d_heightsVisit(&b->heights, lo, hi, visit, ctx);
    else if (b->soup) s3d_soupVisit(b->soup, lo, hi, b->twoSided, visit, ctx);
}

/* ---- a step ---- */
typedef struct S3dHitList { const S3dShape* shape; S3dContact c[16]; int n; } S3dHitList;
static int s3d_collectHit(void* ctx, S3dVec3 a, S3dVec3 b, S3dVec3 c, int twoSided) {
    S3dHitList* L = (S3dHitList*)ctx;
    S3dContact k;
    if (L->n < 16 && s3d_contactTriangle(L->shape, a, b, c, twoSided, &k)) L->c[L->n++] = k;
    return 0;
}
static void s3d_touch(S3dBody* b, int64_t id) {
    int k;
    for (k = 0; k < b->touchN; k++) if (b->touch[k] == id) return;
    if (b->touchN < S3D_TOUCH_MAX) b->touch[b->touchN++] = id;
}

/* The answer to one contact: A (dynamic) is pushed out of B by depth (shared by the masses when B is dynamic too) and the velocities along n meet. */
static void s3d_resolve(S3dBody* A, S3dBody* B, S3dContact c, S3dVec3 bVel) {
    float ia = A->invMass, ib = B && B->kind == S3D_BODY_DYNAMIC ? B->invMass : 0, sum = ia + ib, vn, j, bounce, fric;
    S3dVec3 rel, vt, push;
    if (sum <= 0) return;
    /* a character on ground it can stand on: straight up, so that it does not slide down the slope */
    if (A->character && c.n.y >= A->slopeCos) push = s3d_v3(0, c.depth / c.n.y, 0);
    else push = s3d_scale(c.n, c.depth);
    if (c.n.y >= A->slopeCos) A->onGround = 1;
    A->pos = s3d_add(A->pos, s3d_scale(push, ia / sum));
    if (ib > 0) B->pos = s3d_sub(B->pos, s3d_scale(c.n, c.depth * ib / sum));
    rel = s3d_sub(A->vel, bVel);
    vn = s3d_dot(rel, c.n);
    if (vn >= 0) return;
    bounce = s3d_maxf(A->bounce, B ? B->bounce : 0);
    if (-vn < 0.5f) bounce = 0;   /* (slow: it comes to rest) */
    j = -(1 + bounce) * vn / sum;
    A->vel = s3d_add(A->vel, s3d_scale(c.n, j * ia));
    if (ib > 0) B->vel = s3d_sub(B->vel, s3d_scale(c.n, j * ib));
    if (A->character) return;   /* (a character's sideways velocity is its walk) */
    /* friction: the sideways velocity slows by at most friction * the impulse */
    fric = sqrtf(A->friction * (B ? B->friction : A->friction));
    vt = s3d_sub(rel, s3d_scale(c.n, vn));
    {
        float vtl = s3d_length(vt), jt;
        if (vtl > 1e-6f) {
            S3dVec3 t = s3d_scale(vt, 1 / vtl);
            jt = s3d_minf(vtl / sum, fric * j);
            A->vel = s3d_sub(A->vel, s3d_scale(t, jt * ia));
            if (ib > 0) B->vel = s3d_add(B->vel, s3d_scale(t, jt * ib));
        }
    }
}

/* the contacts of body A (dynamic) with all others, resolved; `test`: only find whether A overlaps anything solid (returns 1) */
static int s3d_contactsOf(S3dBody** bodies, int n, int ai, int test) {
    S3dBody* A = bodies[ai];
    int k, m, found = 0;
    for (k = 0; k < n; k++) {
        S3dBody* B = bodies[k];
        S3dShape sa, sb;
        S3dContact c;
        if (k == ai || (B->kind == S3D_BODY_DYNAMIC && k < ai && !test && !B->trigger && !A->trigger)) continue;   /* (two dynamic ones: once) */
        sa = s3d_bodyShape(A, A->pos);
        if (B->shape == S3D_SHAPE_MESH) {
            S3dHitList L;
            L.shape = &sa; L.n = 0;
            s3d_staticVisit(B, sa.lo, sa.hi, s3d_collectHit, &L);
            if (L.n == 0) continue;
            if (A->trigger || B->trigger) { s3d_touch(A, B->id); s3d_touch(B, A->id); continue; }
            if (test) { for (m = 0; m < L.n; m++) if (L.c[m].depth > 0.002f) return 1; continue; }
            s3d_touch(A, B->id);
            /* the deepest first; each one again after the others moved A */
            for (m = 0; m < L.n; m++) {
                int q, best = m;
                for (q = m + 1; q < L.n; q++) if (L.c[q].depth > L.c[best].depth) best = q;
                c = L.c[best]; L.c[best] = L.c[m]; L.c[m] = c;
                if (m > 0) {   /* how deep it still is: along n, what A moved since */
                    S3dShape now = s3d_bodyShape(A, A->pos);
                    S3dVec3 moved = s3d_sub(now.a, sa.a);
                    c.depth -= s3d_dot(moved, c.n);
                    if (c.depth <= 0) continue;
                }
                s3d_resolve(A, B, c, B->kind == S3D_BODY_KINEMATIC ? B->moved : s3d_v3(0, 0, 0));
            }
            continue;
        }
        sb = s3d_bodyShape(B, B->pos);
        if (!s3d_boxesMeet(sa.lo, sa.hi, sb.lo, sb.hi) || !s3d_contactShapes(&sa, &sb, &c)) continue;
        if (A->trigger || B->trigger) { s3d_touch(A, B->id); s3d_touch(B, A->id); continue; }
        if (test) { if (c.depth > 0.002f) return 1; continue; }
        s3d_touch(A, B->id); s3d_touch(B, A->id);
        found = 1;
        s3d_resolve(A, B, c, B->kind == S3D_BODY_KINEMATIC ? B->moved : B->vel);
        if (B->kind == S3D_BODY_DYNAMIC && B->character && c.n.y <= -B->slopeCos) B->onGround = 1;
    }
    return test ? 0 : found;
}

typedef struct S3dRayHit { float dist; S3dVec3 point, normal; int index; } S3dRayHit;
static int s3d_raycast(S3dBody** bodies, int n, S3dVec3 o, S3dVec3 d, float maxDist, int skip, S3dRayHit* hit);

/* One step of dt seconds for the bodies (gravity: the acceleration of the world, e.g. 0, -9.81, 0). */
static void s3d_physicsStep(S3dBody** bodies, int n, S3dVec3 gravity, float dt) {
    int i, it;
    for (i = 0; i < n; i++) {
        S3dBody* b = bodies[i];
        b->touchN = 0;
        if (b->kind == S3D_BODY_KINEMATIC) { b->pos = s3d_add(b->pos, s3d_scale(b->vel, dt)); continue; }
        if (b->kind != S3D_BODY_DYNAMIC) continue;
        b->wasOnGround = b->onGround;
        b->onGround = 0;
        b->vel = s3d_add(b->vel, s3d_scale(gravity, b->gravity * dt));
        if (b->damping > 0) b->vel = s3d_scale(b->vel, s3d_maxf(0, 1 - b->damping * dt));
        if (b->character) {
            b->vel.x = b->walkX; b->vel.z = b->walkZ;
            if (b->jump > 0) { if (b->wasOnGround) b->vel.y = b->jump; b->jump = 0; }
        }
        b->pos = s3d_add(b->pos, s3d_scale(b->vel, dt));
    }
    for (it = 0; it < 3; it++)
        for (i = 0; i < n; i++) {
            S3dBody* b = bodies[i];
            if (b->kind != S3D_BODY_DYNAMIC) continue;
            if (b->character && it == 0 && b->wasOnGround && (b->walkX != 0 || b->walkZ != 0)) {
                /* a step: blocked sideways where the body is a step higher free - it goes up, and then down onto the step */
                S3dVec3 at = b->pos;
                b->pos.y += b->stepHeight;
                if (!s3d_contactsOf(bodies, n, i, 1)) {
                    S3dRayHit hit;
                    S3dShape s = s3d_bodyShape(b, b->pos);
                    float foot = s.a.y - s.r;
                    if (s3d_raycast(bodies, n, s3d_v3(s.a.x, foot + 0.01f, s.a.z), s3d_v3(0, -1, 0), b->stepHeight + 0.02f, i, &hit) >= 0 && hit.normal.y >= b->slopeCos) {
                        b->pos.y -= s3d_maxf(0, hit.dist - 0.011f);
                        if (s3d_contactsOf(bodies, n, i, 1)) b->pos = at;
                        else { b->onGround = 1; if (b->vel.y < 0) b->vel.y = 0; s3d_touch(b, bodies[hit.index]->id); }
                    } else b->pos = at;   /* (nothing to stand on below: no step, it may fall) */
                } else b->pos = at;
            }
            s3d_contactsOf(bodies, n, i, 0);
        }
}

/* ---- rays ---- */
typedef struct S3dRay { S3dVec3 o, d; float best; S3dVec3 n; } S3dRay;
static int s3d_rayTriangle(void* ctx, S3dVec3 a, S3dVec3 b, S3dVec3 c, int twoSided) {
    S3dRay* r = (S3dRay*)ctx;
    S3dVec3 e1 = s3d_sub(b, a), e2 = s3d_sub(c, a), p = s3d_cross(r->d, e2), q, tv;
    float det = s3d_dot(e1, p), inv, u, v, t;
    (void)twoSided;
    if (fabsf(det) < 1e-12f) return 0;
    inv = 1 / det; tv = s3d_sub(r->o, a);
    u = s3d_dot(tv, p) * inv;
    if (u < 0 || u > 1) return 0;
    q = s3d_cross(tv, e1);
    v = s3d_dot(r->d, q) * inv;
    if (v < 0 || u + v > 1) return 0;
    t = s3d_dot(e2, q) * inv;
    if (t >= 0 && t < r->best) {
        r->best = t;
        r->n = s3d_normalize(s3d_cross(e1, e2));
        if (s3d_dot(r->n, r->d) > 0) r->n = s3d_scale(r->n, -1);
    }
    return 0;
}
/* a ray against a box (slabs): the distance or -1, the normal of the face */
static float s3d_rayBox(S3dVec3 o, S3dVec3 d, S3dVec3 lo, S3dVec3 hi, S3dVec3* n) {
    float tmin = -1e30f, tmax = 1e30f, oo[3], dd[3], l[3], h[3];
    int k, axis = 1;
    oo[0] = o.x; oo[1] = o.y; oo[2] = o.z; dd[0] = d.x; dd[1] = d.y; dd[2] = d.z; l[0] = lo.x; l[1] = lo.y; l[2] = lo.z; h[0] = hi.x; h[1] = hi.y; h[2] = hi.z;
    for (k = 0; k < 3; k++) {
        if (fabsf(dd[k]) < 1e-12f) { if (oo[k] < l[k] || oo[k] > h[k]) return -1; continue; }
        {
            float t1 = (l[k] - oo[k]) / dd[k], t2 = (h[k] - oo[k]) / dd[k];
            if (t1 > t2) { float s = t1; t1 = t2; t2 = s; }
            if (t1 > tmin) { tmin = t1; axis = k; }
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return -1;
        }
    }
    if (tmax < 0) return -1;
    if (tmin < 0) { *n = s3d_scale(d, -1); return 0; }   /* (inside) */
    *n = s3d_v3(axis == 0 ? (dd[0] > 0 ? -1.0f : 1.0f) : 0, axis == 1 ? (dd[1] > 0 ? -1.0f : 1.0f) : 0, axis == 2 ? (dd[2] > 0 ? -1.0f : 1.0f) : 0);
    return tmin;
}
/* a ray against a sphere at c */
static float s3d_raySphere(S3dVec3 o, S3dVec3 d, S3dVec3 c, float r) {
    S3dVec3 m = s3d_sub(o, c);
    float b = s3d_dot(m, d), cc = s3d_dot(m, m) - r * r, disc, t;
    if (cc > 0 && b > 0) return -1;
    disc = b * b - cc;
    if (disc < 0) return -1;
    t = -b - sqrtf(disc);
    return t < 0 ? 0 : t;
}
/* a ray against an upright capsule (segment a..b, a below): the side (a cylinder), then the two spheres */
static float s3d_rayCapsule(S3dVec3 o, S3dVec3 d, S3dVec3 a, S3dVec3 b, float r, S3dVec3* n) {
    float best = -1, t;
    float A = d.x * d.x + d.z * d.z, ox = o.x - a.x, oz = o.z - a.z, B = ox * d.x + oz * d.z, C = ox * ox + oz * oz - r * r;
    if (A > 1e-12f) {
        float disc = B * B - A * C;
        if (disc >= 0) {
            t = (-B - sqrtf(disc)) / A;
            if (t >= 0) {
                float y = o.y + d.y * t;
                if (y >= a.y && y <= b.y) { best = t; *n = s3d_normalize(s3d_v3(o.x + d.x * t - a.x, 0, o.z + d.z * t - a.z)); }
            }
        }
    }
    t = s3d_raySphere(o, d, a, r);
    if (t >= 0 && (best < 0 || t < best)) { best = t; *n = s3d_normalize(s3d_sub(s3d_add(o, s3d_scale(d, t)), a)); }
    t = s3d_raySphere(o, d, b, r);
    if (t >= 0 && (best < 0 || t < best)) { best = t; *n = s3d_normalize(s3d_sub(s3d_add(o, s3d_scale(d, t)), b)); }
    return best;
}
/* The nearest body a ray meets (d: a unit vector; up to maxDist; not body `skip`, not triggers): its index or -1. */
static int s3d_raycast(S3dBody** bodies, int n, S3dVec3 o, S3dVec3 d, float maxDist, int skip, S3dRayHit* hit) {
    int k, found = -1;
    float best = maxDist;
    S3dVec3 bestN = s3d_v3(0, 1, 0), end = s3d_add(o, s3d_scale(d, maxDist));
    S3dVec3 rlo = s3d_v3(s3d_minf(o.x, end.x), s3d_minf(o.y, end.y), s3d_minf(o.z, end.z)), rhi = s3d_v3(s3d_maxf(o.x, end.x), s3d_maxf(o.y, end.y), s3d_maxf(o.z, end.z));
    for (k = 0; k < n; k++) {
        S3dBody* b = bodies[k];
        S3dVec3 nn = s3d_v3(0, 1, 0);
        float t = -1;
        if (k == skip || b->trigger) continue;
        if (b->shape == S3D_SHAPE_MESH) {
            S3dRay r;
            r.o = o; r.d = d; r.best = best; r.n = nn;
            s3d_staticVisit(b, rlo, rhi, s3d_rayTriangle, &r);
            if (r.best < best) { t = r.best; nn = r.n; }
        } else {
            S3dShape s = s3d_bodyShape(b, b->pos);
            if (!s3d_boxesMeet(rlo, rhi, s.lo, s.hi)) continue;
            if (s.box) t = s3d_rayBox(o, d, s.lo, s.hi, &nn);
            else if (b->shape == S3D_SHAPE_SPHERE) { t = s3d_raySphere(o, d, s.a, s.r); if (t >= 0) nn = s3d_normalize(s3d_sub(s3d_add(o, s3d_scale(d, t)), s.a)); }
            else t = s3d_rayCapsule(o, d, s.a, s.b, s.r, &nn);
        }
        if (t >= 0 && t < best) { best = t; bestN = nn; found = k; }
    }
    if (found >= 0) { hit->dist = best; hit->point = s3d_add(o, s3d_scale(d, best)); hit->normal = bestN; hit->index = found; }
    return found;
}

/* The inverse of a model matrix (rotation, scale, move; no projection). */
static S3dMat4 s3d_affineInverse(const S3dMat4* m) {
    S3dMat4 r = s3d_identity();
    float a = S3D_AT(*m, 0, 0), b = S3D_AT(*m, 0, 1), c = S3D_AT(*m, 0, 2), d = S3D_AT(*m, 1, 0), e = S3D_AT(*m, 1, 1), f = S3D_AT(*m, 1, 2), g = S3D_AT(*m, 2, 0), h = S3D_AT(*m, 2, 1), i = S3D_AT(*m, 2, 2);
    float A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g, det = a * A + b * B + c * C, inv;
    S3dVec3 t;
    if (fabsf(det) < 1e-20f) return r;
    inv = 1 / det;
    S3D_AT(r, 0, 0) = A * inv; S3D_AT(r, 0, 1) = -(b * i - c * h) * inv; S3D_AT(r, 0, 2) = (b * f - c * e) * inv;
    S3D_AT(r, 1, 0) = B * inv; S3D_AT(r, 1, 1) = (a * i - c * g) * inv; S3D_AT(r, 1, 2) = -(a * f - c * d) * inv;
    S3D_AT(r, 2, 0) = C * inv; S3D_AT(r, 2, 1) = -(a * h - b * g) * inv; S3D_AT(r, 2, 2) = (a * e - b * d) * inv;
    t = s3d_transformDir(&r, s3d_v3(S3D_AT(*m, 0, 3), S3D_AT(*m, 1, 3), S3D_AT(*m, 2, 3)));
    S3D_AT(r, 0, 3) = -t.x; S3D_AT(r, 1, 3) = -t.y; S3D_AT(r, 2, 3) = -t.z;
    return r;
}

#endif /* FIRE_S3D_PHYSICS_H */
