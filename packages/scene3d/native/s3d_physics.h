/* fire scene3d: physics (scene3d.physics, docs/graphics/physics.md) - simple, small and the same everywhere.
 *
 * Bodies: static (the ground, walls: they never move), dynamic (they fall, bounce, tumble, are pushed) and kinematic (moved by the program or by their
 * velocity, they push dynamic bodies and are not pushed). Shapes: a box, a sphere, an upright capsule (figures) - and for static bodies the triangles of
 * the object itself (a mesh, in an xz grid so that only the near ones are tested) or the heights of a terrain (no triangles in memory: those of the cells
 * below a body are made when they are needed).
 * Dynamic boxes and spheres turn (an orientation and an angular velocity; boxes tumble, balls roll); contacts have points (up to 8 a pair: a box lies
 * on its four corners), impulses act at the points. A step: gravity and damping, moving and turning, then the pairs that the sorted boxes find are
 * answered three times (pushed apart, the velocity at each point reflected by the bounce, the sideways velocity slowed by the friction), and the joints
 * (rope, rod, spring). A body that rests half a second sleeps until something touches it. A character is an upright capsule that walks (its sideways
 * velocity is set), jumps, climbs steps up to a height and stands on slopes up to an angle.
 * Everything is float and deterministic: the same steps give the same positions in the VM and in a native build. */
#ifndef FIRE_S3D_PHYSICS_H
#define FIRE_S3D_PHYSICS_H

#ifndef FIRE_S3D_ENGINE_H
#include "s3d_engine.h"
#endif

enum { S3D_BODY_STATIC = 0, S3D_BODY_DYNAMIC = 1, S3D_BODY_KINEMATIC = 2 };
enum { S3D_SHAPE_MESH = 0, S3D_SHAPE_BOX = 1, S3D_SHAPE_SPHERE = 2, S3D_SHAPE_CAPSULE = 3 };
enum { S3D_JOINT_ROPE = 0, S3D_JOINT_ROD = 1, S3D_JOINT_SPRING = 2 };
#define S3D_TOUCH_MAX 8
#define S3D_MANIFOLD 8

/* triangles in the world, in an xz grid of cells (each cell lists the triangles whose box reaches into it) */
typedef struct S3dSoup {
    float* v; s3d_size vN;           /* vertices x, y, z */
    uint32_t* t; s3d_size tN;        /* 3 indices a triangle (tN: the number of triangles) */
    S3dVec3 lo, hi;
    int gx, gz; float cx, cz;        /* cells and their size */
    uint32_t* start; uint32_t* list; /* start[cell] .. start[cell + 1]: the triangles of a cell in `list` */
} S3dSoup;

/* the heights of a terrain (see fire_s3d_terrainMesh: local x, z centred, cell size, height step; hard: blocks with walls), where it stands, its box in the world */
typedef struct S3dHeights { const float* H; int cols, rows, hard; float cell, step, minH; S3dMat4 model, inv; S3dVec3 lo, hi; } S3dHeights;

typedef struct S3dBody {
    int kind, shape, trigger, character, twoSided, rotates, asleep;
    S3dVec3 c;                       /* the centre of the shape in the world */
    float q[4], R[9];                /* its turn: a quaternion (w, x, y, z) and the matrix of it (rows); (1, 0, 0, 0) for a body that does not turn */
    S3dVec3 local, half;             /* the centre from the object's position (in the body's own turn); a box: half its sizes; a sphere: half.x the radius;
                                      * a capsule: half.x the radius, half.y half its middle */
    S3dVec3 vel, w, moved;           /* velocity, angular velocity (rad/s, world); a kinematic body: its velocity from how the program moved it */
    S3dVec3 invI;                    /* 1 / the moments of inertia around its own axes (0: it does not turn) */
    float invMass, bounce, friction, damping, angularDamping, gravity, sleepTime;
    S3dSoup* soup; int hasHeights; S3dHeights heights;
    float key[12];                   /* where the object stood when its shape was made */
    float walkX, walkZ, stepHeight, slopeCos, jump; int onGround, wasOnGround;
    int64_t id; int64_t touch[S3D_TOUCH_MAX]; int touchN;
} S3dBody;

/* a joint: a rope (at most `length` apart), a rod (exactly), a spring (pulls towards `length`); the anchors in the bodies' own turn - b NULL: lb is a point of the world */
typedef struct S3dJoint { int kind; S3dBody* a; S3dBody* b; S3dVec3 la, lb; float length, stiffness, damping; } S3dJoint;

/* a shape in the world for a moment: a box (centre, axes, half sizes) or a segment a..b with a radius (a sphere: a == b); lo..hi its box along the axes */
typedef struct S3dShape { int box; S3dVec3 c, ax[3], h, lo, hi, a, b; float r; } S3dShape;
/* a contact: n the way the first shape is pushed out, p the point (in the world), depth how far they overlap */
typedef struct S3dContact { S3dVec3 n, p; float depth; } S3dContact;

/* ---- turns ---- */
static void s3d_qMatrix(const float* q, float* R) {
    float w = q[0], x = q[1], y = q[2], z = q[3];
    R[0] = 1 - 2 * (y * y + z * z); R[1] = 2 * (x * y - w * z); R[2] = 2 * (x * z + w * y);
    R[3] = 2 * (x * y + w * z); R[4] = 1 - 2 * (x * x + z * z); R[5] = 2 * (y * z - w * x);
    R[6] = 2 * (x * z - w * y); R[7] = 2 * (y * z + w * x); R[8] = 1 - 2 * (x * x + y * y);
}
static void s3d_qMul(const float* a, const float* b, float* o) {
    float w = a[0] * b[0] - a[1] * b[1] - a[2] * b[2] - a[3] * b[3], x = a[0] * b[1] + a[1] * b[0] + a[2] * b[3] - a[3] * b[2];
    float y = a[0] * b[2] - a[1] * b[3] + a[2] * b[0] + a[3] * b[1], z = a[0] * b[3] + a[1] * b[2] - a[2] * b[1] + a[3] * b[0];
    o[0] = w; o[1] = x; o[2] = y; o[3] = z;
}
static void s3d_qNormalize(float* q) {
    float l = sqrtf(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
    if (l < 1e-12f) { q[0] = 1; q[1] = q[2] = q[3] = 0; return; }
    q[0] /= l; q[1] /= l; q[2] /= l; q[3] /= l;
}
/* the turn of an object (y, then x, then z: s3d_rotationXYZ) as a quaternion */
static void s3d_qFromEuler(float rx, float ry, float rz, float* q) {
    float qx[4], qy[4], qz[4], t[4];
    qx[0] = cosf(rx * 0.5f); qx[1] = sinf(rx * 0.5f); qx[2] = qx[3] = 0;
    qy[0] = cosf(ry * 0.5f); qy[2] = sinf(ry * 0.5f); qy[1] = qy[3] = 0;
    qz[0] = cosf(rz * 0.5f); qz[3] = sinf(rz * 0.5f); qz[1] = qz[2] = 0;
    s3d_qMul(qy, qx, t); s3d_qMul(t, qz, q);
    s3d_qNormalize(q);
}
/* and back: the angles of the object from a turn matrix (R = Ry * Rx * Rz) */
static void s3d_eulerOf(const float* R, float* rx, float* ry, float* rz) {
    float s = -R[5];
    s = s < -1 ? -1 : s > 1 ? 1 : s;
    *rx = asinf(s);
    if (fabsf(s) < 0.9999f) { *ry = atan2f(R[2], R[8]); *rz = atan2f(R[3], R[4]); }
    else { *ry = atan2f(-R[6], R[0]); *rz = 0; }
}
static S3dVec3 s3d_rot(const float* R, S3dVec3 v) { return s3d_v3(R[0] * v.x + R[1] * v.y + R[2] * v.z, R[3] * v.x + R[4] * v.y + R[5] * v.z, R[6] * v.x + R[7] * v.y + R[8] * v.z); }
static S3dVec3 s3d_rotT(const float* R, S3dVec3 v) { return s3d_v3(R[0] * v.x + R[3] * v.y + R[6] * v.z, R[1] * v.x + R[4] * v.y + R[7] * v.z, R[2] * v.x + R[5] * v.y + R[8] * v.z); }

static S3dBody* s3d_bodyNew(void) {
    S3dBody* b = (S3dBody*)s3d_calloc(1, sizeof(S3dBody));
    if (b == NULL) return NULL;
    b->invMass = 1; b->bounce = 0.1f; b->friction = 0.5f; b->gravity = 1; b->stepHeight = 0.3f; b->slopeCos = 0.7071f; b->angularDamping = 0.3f;
    b->q[0] = 1; s3d_qMatrix(b->q, b->R);
    return b;
}
static void s3d_soupFree(S3dSoup* s) { if (s) { S3D_FREE(s->v); S3D_FREE(s->t); S3D_FREE(s->start); S3D_FREE(s->list); S3D_FREE(s); } }
static void s3d_bodyFree(S3dBody* b) { if (b) { s3d_soupFree(b->soup); S3D_FREE(b); } }

/* the moments of inertia from the shape and the mass (a box: m (b^2 + c^2) / 3 with half sizes; a sphere: 2/5 m r^2) */
static void s3d_bodyInertia(S3dBody* b) {
    float m = b->invMass > 0 ? 1 / b->invMass : 0, ix, iy, iz;
    b->invI = s3d_v3(0, 0, 0);
    if (!b->rotates || m <= 0 || b->kind != S3D_BODY_DYNAMIC) return;
    if (b->shape == S3D_SHAPE_SPHERE) { ix = iy = iz = 0.4f * m * b->half.x * b->half.x; }
    else if (b->shape == S3D_SHAPE_BOX) {
        ix = m * (b->half.y * b->half.y + b->half.z * b->half.z) / 3; iy = m * (b->half.x * b->half.x + b->half.z * b->half.z) / 3; iz = m * (b->half.x * b->half.x + b->half.y * b->half.y) / 3;
    } else return;
    b->invI = s3d_v3(ix > 1e-9f ? 1 / ix : 0, iy > 1e-9f ? 1 / iy : 0, iz > 1e-9f ? 1 / iz : 0);
}

static S3dVec3 s3d_boxAxis(const float* R, int k) { return s3d_v3(R[k], R[3 + k], R[6 + k]); }
static float s3d_boxR(const S3dShape* s, S3dVec3 n) { return s->h.x * fabsf(s3d_dot(s->ax[0], n)) + s->h.y * fabsf(s3d_dot(s->ax[1], n)) + s->h.z * fabsf(s3d_dot(s->ax[2], n)); }
static S3dVec3 s3d_toBox(const S3dShape* s, S3dVec3 p) { S3dVec3 d = s3d_sub(p, s->c); return s3d_v3(s3d_dot(d, s->ax[0]), s3d_dot(d, s->ax[1]), s3d_dot(d, s->ax[2])); }
static S3dVec3 s3d_fromBox(const S3dShape* s, S3dVec3 l) { return s3d_add(s->c, s3d_add(s3d_scale(s->ax[0], l.x), s3d_add(s3d_scale(s->ax[1], l.y), s3d_scale(s->ax[2], l.z)))); }
static void s3d_boxCorners(const S3dShape* s, S3dVec3* v) {
    int k;
    for (k = 0; k < 8; k++) v[k] = s3d_fromBox(s, s3d_v3((k & 1) ? s->h.x : -s->h.x, (k & 2) ? s->h.y : -s->h.y, (k & 4) ? s->h.z : -s->h.z));
}

static S3dShape s3d_bodyShape(const S3dBody* b) {
    S3dShape s;
    memset(&s, 0, sizeof s);
    s.c = b->c;
    if (b->shape == S3D_SHAPE_BOX || b->shape == S3D_SHAPE_MESH) {
        S3dVec3 e;
        int k;
        s.box = 1; s.h = b->half;
        for (k = 0; k < 3; k++) s.ax[k] = s3d_boxAxis(b->R, k);
        e = s3d_v3(s3d_boxR(&s, s3d_v3(1, 0, 0)), s3d_boxR(&s, s3d_v3(0, 1, 0)), s3d_boxR(&s, s3d_v3(0, 0, 1)));
        s.lo = s3d_sub(s.c, e); s.hi = s3d_add(s.c, e); s.a = s.b = s.c;
        return s;
    }
    s.r = b->half.x;
    s.a = s.b = s.c;
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

/* ---- contacts: each function fills up to S3D_MANIFOLD contacts (all with the same n and depth) and returns how many ---- */
static int s3d_inBoxShape(const S3dShape* s, S3dVec3 p, float margin) {
    S3dVec3 l = s3d_toBox(s, p);
    return fabsf(l.x) <= s->h.x + margin && fabsf(l.y) <= s->h.y + margin && fabsf(l.z) <= s->h.z + margin;
}
/* a polygon (up to 8 points) cut by the plane dot(p, n) <= d */
static int s3d_clipPoly(const S3dVec3* in, int count, S3dVec3 n, float d, S3dVec3* out) {
    int k, m = 0;
    for (k = 0; k < count; k++) {
        S3dVec3 p = in[k], q = in[(k + 1) % count];
        float sp = s3d_dot(p, n) - d, sq = s3d_dot(q, n) - d;
        if (sp <= 0 && m < 8) out[m++] = p;
        if ((sp < 0) != (sq < 0) && m < 8) out[m++] = s3d_add(p, s3d_scale(s3d_sub(q, p), sp / (sp - sq)));
    }
    return m;
}
/* a box against a box: the 15 separating axes; on a face the other box's face that looks at it is cut by the face's sides - its corners up to 2 cm
 * apart are the points (a box lying on a box: four points, it stays flat); an edge on an edge: one point between them */
static int s3d_boxBox(const S3dShape* A, const S3dShape* B, S3dContact* out) {
    S3dVec3 d = s3d_sub(A->c, B->c), n = s3d_v3(0, 1, 0), L;
    float best = 1e30f, depth = 0;
    int k, m = 0, axis = -1;
    for (k = 0; k < 15; k++) {
        float len, ov, side, score;
        L = k < 3 ? A->ax[k] : k < 6 ? B->ax[k - 3] : s3d_cross(A->ax[(k - 6) / 3], B->ax[(k - 6) % 3]);
        len = s3d_length(L);
        if (len < 1e-5f) continue;
        L = s3d_scale(L, 1 / len);
        side = s3d_dot(d, L);
        ov = s3d_boxR(A, L) + s3d_boxR(B, L) - fabsf(side);
        if (ov <= 0) return 0;
        /* (faces before edges; of two faces about as good the one of B - the one below, mostly - so that the normal does not flip between them) */
        score = k < 3 ? ov * 1.02f + 0.0005f : k < 6 ? ov : ov * 1.05f + 0.001f;
        if (score < best) { best = score; depth = ov; n = side < 0 ? s3d_scale(L, -1) : L; axis = k; }
    }
    if (axis < 6) {
        const S3dShape* ref = axis < 3 ? A : B;
        const S3dShape* inc = axis < 3 ? B : A;
        S3dVec3 nr = axis < 3 ? s3d_scale(n, -1) : n;   /* the reference face's normal, towards the other box */
        S3dVec3 poly[8], tmp[8], u, v, fc, ic, iu, iv, in;
        float hu, hv, hn, ihu, ihv, dmax = -1e30f;
        (void)dmax;
        int r = axis % 3, ia = 0, ru = (r + 1) % 3, rv = (r + 2) % 3, iua, iva, cnt;
        float hh[3], ih[3];
        hh[0] = ref->h.x; hh[1] = ref->h.y; hh[2] = ref->h.z; ih[0] = inc->h.x; ih[1] = inc->h.y; ih[2] = inc->h.z;
        u = ref->ax[ru]; v = ref->ax[rv]; hu = hh[ru]; hv = hh[rv]; hn = hh[r];
        fc = s3d_add(ref->c, s3d_scale(nr, hn));
        /* the incident face: the face of the other box most against nr */
        for (k = 1; k < 3; k++) if (fabsf(s3d_dot(inc->ax[k], nr)) > fabsf(s3d_dot(inc->ax[ia], nr))) ia = k;
        in = s3d_dot(inc->ax[ia], nr) > 0 ? s3d_scale(inc->ax[ia], -1) : inc->ax[ia];
        ic = s3d_add(inc->c, s3d_scale(in, ih[ia]));
        iua = (ia + 1) % 3; iva = (ia + 2) % 3;
        iu = inc->ax[iua]; iv = inc->ax[iva]; ihu = ih[iua]; ihv = ih[iva];
        poly[0] = s3d_add(ic, s3d_add(s3d_scale(iu, ihu), s3d_scale(iv, ihv)));
        poly[1] = s3d_add(ic, s3d_add(s3d_scale(iu, -ihu), s3d_scale(iv, ihv)));
        poly[2] = s3d_add(ic, s3d_add(s3d_scale(iu, -ihu), s3d_scale(iv, -ihv)));
        poly[3] = s3d_add(ic, s3d_add(s3d_scale(iu, ihu), s3d_scale(iv, -ihv)));
        cnt = 4;
        cnt = s3d_clipPoly(poly, cnt, u, s3d_dot(ref->c, u) + hu, tmp);
        cnt = s3d_clipPoly(tmp, cnt, s3d_scale(u, -1), -s3d_dot(ref->c, u) + hu, poly);
        cnt = s3d_clipPoly(poly, cnt, v, s3d_dot(ref->c, v) + hv, tmp);
        cnt = s3d_clipPoly(tmp, cnt, s3d_scale(v, -1), -s3d_dot(ref->c, v) + hv, poly);
        for (k = 0; k < cnt && m < S3D_MANIFOLD; k++) {
            float sep = s3d_dot(s3d_sub(poly[k], fc), nr);
            if (sep > 0.02f) continue;
            out[m].p = poly[k]; out[m].depth = s3d_minf(-sep, depth); out[m].n = n;
            if (-sep > dmax) dmax = -sep;
            m++;
        }
        if (m > 0) return m;
    }
    if (m == 0) {   /* edge on edge: between the corner of A deepest along -n and the corner of B deepest along n */
        S3dVec3 va[8], vb[8];
        int ka = 0, kb = 0;
        s3d_boxCorners(A, va); s3d_boxCorners(B, vb);
        for (k = 1; k < 8; k++) { if (s3d_dot(va[k], n) < s3d_dot(va[ka], n)) ka = k; if (s3d_dot(vb[k], n) > s3d_dot(vb[kb], n)) kb = k; }
        out[0].p = s3d_scale(s3d_add(va[ka], vb[kb]), 0.5f);
        m = 1;
    }
    out[0].n = n; out[0].depth = depth;
    return 1;
}
/* a sphere or capsule against a box: in the box's own space (n pushes the round one out, p on the box) */
static int s3d_roundBox(const S3dShape* R, const S3dShape* B, S3dContact* c) {
    S3dVec3 a = s3d_toBox(B, R->a), b = s3d_toBox(B, R->b), lo = s3d_scale(B->h, -1), hi = B->h, p = s3d_onSegment(a, b, s3d_v3(0, 0, 0)), q, d, nl;
    float dist;
    int k;
    for (k = 0; k < 3; k++) { q = s3d_inBox(p, lo, hi); p = s3d_onSegment(a, b, q); }
    q = s3d_inBox(p, lo, hi);
    d = s3d_sub(p, q);
    dist = s3d_length(d);
    if (dist > R->r) return 0;
    if (dist > 1e-6f) { nl = s3d_scale(d, 1 / dist); c->depth = R->r - dist; }
    else {   /* the middle of the round one inside the box: out by the nearest face */
        float f[6];
        int best = 0;
        f[0] = p.x - lo.x; f[1] = hi.x - p.x; f[2] = p.y - lo.y; f[3] = hi.y - p.y; f[4] = p.z - lo.z; f[5] = hi.z - p.z;
        for (k = 1; k < 6; k++) if (f[k] < f[best]) best = k;
        nl = s3d_v3(best == 0 ? -1.0f : best == 1 ? 1.0f : 0, best == 2 ? -1.0f : best == 3 ? 1.0f : 0, best == 4 ? -1.0f : best == 5 ? 1.0f : 0);
        c->depth = f[best] + R->r;
    }
    c->n = s3d_add(s3d_scale(B->ax[0], nl.x), s3d_add(s3d_scale(B->ax[1], nl.y), s3d_scale(B->ax[2], nl.z)));
    c->p = s3d_fromBox(B, q);
    return 1;
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
    c->p = s3d_add(pb, s3d_scale(c->n, B->r));
    return 1;
}
static int s3d_collide(const S3dShape* A, const S3dShape* B, S3dContact* c) {
    if (A->box && B->box) return s3d_boxBox(A, B, c);
    if (!A->box && !B->box) return s3d_roundRound(A, B, c);
    if (!A->box) return s3d_roundBox(A, B, c);
    if (!s3d_roundBox(B, A, c)) return 0;
    c->n = s3d_scale(c->n, -1);
    return 1;
}

/* p (in the plane of the triangle) inside it, with a little room */
static int s3d_inTriangle(S3dVec3 p, S3dVec3 a, S3dVec3 b, S3dVec3 c, S3dVec3 n) {
    return s3d_dot(s3d_cross(s3d_sub(b, a), s3d_sub(p, a)), n) >= -1e-4f && s3d_dot(s3d_cross(s3d_sub(c, b), s3d_sub(p, b)), n) >= -1e-4f
        && s3d_dot(s3d_cross(s3d_sub(a, c), s3d_sub(p, c)), n) >= -1e-4f;
}
/* a shape against a triangle (n: its front, counter clockwise; twoSided: both sides push out) - up to 4 contacts */
static int s3d_contactTriangle(const S3dShape* A, S3dVec3 a, S3dVec3 b, S3dVec3 c, int twoSided, S3dContact* out) {
    S3dVec3 n = s3d_cross(s3d_sub(b, a), s3d_sub(c, a));
    float nl = s3d_length(n);
    if (nl < 1e-12f) return 0;
    n = s3d_scale(n, 1 / nl);
    if (A->box) {   /* separating axes: the box's three, the face, the nine crossings of edges and box axes */
        S3dVec3 tri[3], axis, bestAxis = n, v[8];
        float best = 1e30f, bestScore = 1e30f, side, er = s3d_boxR(A, n);
        int k, j, m = 0;
        tri[0] = a; tri[1] = b; tri[2] = c;
        side = s3d_dot(s3d_sub(A->c, a), n);
        if (!twoSided && side < -er) return 0;   /* wholly behind */
        for (k = 0; k < 13; k++) {
            float len, tmin, tmax, rb, pc, ov, t;
            if (k < 3) axis = A->ax[k];
            else if (k == 3) axis = n;
            else axis = s3d_cross(s3d_sub(tri[((k - 4) / 3 + 1) % 3], tri[(k - 4) / 3]), A->ax[(k - 4) % 3]);
            len = s3d_length(axis);
            if (len < 1e-6f) continue;
            axis = s3d_scale(axis, 1 / len);
            tmin = tmax = s3d_dot(tri[0], axis);
            for (j = 1; j < 3; j++) { t = s3d_dot(tri[j], axis); if (t < tmin) tmin = t; if (t > tmax) tmax = t; }
            rb = s3d_boxR(A, axis);
            pc = s3d_dot(A->c, axis);
            ov = s3d_minf(tmax - (pc - rb), (pc + rb) - tmin);
            if (ov <= 0) return 0;
            /* (the face is the better way out when it is about as good) */
            if ((k == 3 ? ov * 0.95f : ov) < bestScore) { bestScore = k == 3 ? ov * 0.95f : ov; best = ov; bestAxis = s3d_dot(s3d_sub(A->c, a), axis) < 0 ? s3d_scale(axis, -1) : axis; }
        }
        if (!twoSided && s3d_dot(bestAxis, n) < -0.01f) { bestAxis = n; best = s3d_dot(n, s3d_sub(a, A->c)) + er; }
        if (best <= 0) return 0;
        /* the points: the corners of the box behind the face, inside the triangle (a box on the ground: its four lower corners) */
        s3d_boxCorners(A, v);
        if (fabsf(s3d_dot(bestAxis, n)) > 0.9f) {
            S3dVec3 fn = s3d_dot(bestAxis, n) > 0 ? n : s3d_scale(n, -1);
            for (k = 0; k < 8 && m < 4; k++) {
                float sd = s3d_dot(s3d_sub(v[k], a), fn);
                if (sd < 0.02f && s3d_inTriangle(s3d_sub(v[k], s3d_scale(fn, sd)), a, b, c, n)) { out[m].p = v[k]; out[m].n = fn; out[m].depth = -sd; m++; }
            }
        }
        if (m == 0) {
            int lo = 0;
            for (k = 1; k < 8; k++) if (s3d_dot(v[k], bestAxis) < s3d_dot(v[lo], bestAxis)) lo = k;
            out[0].p = v[lo]; out[0].n = bestAxis; out[0].depth = best; m = 1;
        }
        return m;
    } else {
        S3dVec3 ps, pt, d;
        float dist, s;
        s3d_segTriangle(A->a, A->b, a, b, c, n, &ps, &pt);
        d = s3d_sub(ps, pt);
        dist = s3d_length(d);
        if (dist >= A->r) return 0;
        s = s3d_dot(s3d_sub(ps, a), n);
        out->p = pt;
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
/* the box of a body in the world (a static one of triangles: the box of all of them) */
static void s3d_bodyBox(const S3dBody* b, S3dVec3* lo, S3dVec3* hi) {
    if (b->shape == S3D_SHAPE_MESH) {
        if (b->hasHeights) { *lo = b->heights.lo; *hi = b->heights.hi; }
        else if (b->soup) { *lo = b->soup->lo; *hi = b->soup->hi; }
        else { *lo = *hi = b->c; }
        return;
    }
    { S3dShape s = s3d_bodyShape(b); *lo = s.lo; *hi = s.hi; }
}
static int s3d_dynamic(const S3dBody* b) { return b->kind == S3D_BODY_DYNAMIC; }
static void s3d_wake(S3dBody* b) { b->asleep = 0; b->sleepTime = 0; }

/* the velocity of a body at the point centre + r, the effect of an impulse along n there (its "mass" seen at the point), an impulse */
static S3dVec3 s3d_velAt(const S3dBody* b, S3dVec3 r) {
    if (b->kind == S3D_BODY_KINEMATIC) return b->moved;
    if (b->kind != S3D_BODY_DYNAMIC) return s3d_v3(0, 0, 0);
    return b->rotates ? s3d_add(b->vel, s3d_cross(b->w, r)) : b->vel;
}
static S3dVec3 s3d_invIW(const S3dBody* b, S3dVec3 v) {
    S3dVec3 l = s3d_rotT(b->R, v);
    return s3d_rot(b->R, s3d_v3(l.x * b->invI.x, l.y * b->invI.y, l.z * b->invI.z));
}
static float s3d_kAt(const S3dBody* b, S3dVec3 r, S3dVec3 n) {
    if (b->kind != S3D_BODY_DYNAMIC) return 0;
    if (!b->rotates) return b->invMass;
    return b->invMass + s3d_dot(n, s3d_cross(s3d_invIW(b, s3d_cross(r, n)), r));
}
static void s3d_impulse(S3dBody* b, S3dVec3 r, S3dVec3 J) {
    if (b->kind != S3D_BODY_DYNAMIC) return;
    b->vel = s3d_add(b->vel, s3d_scale(J, b->invMass));
    if (b->rotates) b->w = s3d_add(b->w, s3d_invIW(b, s3d_cross(r, J)));
}

static void s3d_touch(S3dBody* b, int64_t id) {
    int k;
    for (k = 0; k < b->touchN; k++) if (b->touch[k] == id) return;
    if (b->touchN < S3D_TOUCH_MAX) b->touch[b->touchN++] = id;
}

/* The answer to the contacts of a pair: A (dynamic) is pushed out of B by the depth (shared by the masses when B is dynamic too), then at each point the
 * velocities along n meet (with the bounce) and the sideways velocity is slowed by the friction. */
/* a body turned by a small angle (the vector: axis times angle) */
static void s3d_turnBy(S3dBody* b, S3dVec3 a) {
    float wq[4], dq[4];
    int k;
    wq[0] = 0; wq[1] = a.x; wq[2] = a.y; wq[3] = a.z;
    s3d_qMul(wq, b->q, dq);
    for (k = 0; k < 4; k++) b->q[k] += dq[k] * 0.5f;
    s3d_qNormalize(b->q);
    s3d_qMatrix(b->q, b->R);
}
/* the impulses a pair got in this step, point by point (the sum along n never below 0; the friction at most `friction` times it) */
#define S3D_ACC 16
typedef struct S3dAcc { float jn[S3D_ACC]; S3dVec3 jt[S3D_ACC]; } S3dAcc;
static void s3d_resolve(S3dBody* A, S3dBody* B, const S3dContact* c, int m, S3dAcc* acc, int slot, float dt) {
    float ia = A->invMass, ib = s3d_dynamic(B) ? B->invMass : 0, sum = ia + ib, bounce, fric, depth = 0;
    S3dVec3 n = c[0].n, push;
    int k, turning;
    if (sum <= 0) return;
    for (k = 0; k < m; k++) if (c[k].depth > depth) depth = c[k].depth;
    if (n.y >= A->slopeCos) A->onGround = 1;
    if (ib > 0 && -n.y >= B->slopeCos) B->onGround = 1;
    turning = A->rotates || (ib > 0 && B->rotates);
    if (turning) {
        /* bodies that turn are not moved here: the push out goes into the velocity at each point (below) - so the friction holds it too */
    } else {
        /* (2 mm of overlap stay: the contact is still there in the next step - the body stands, it does not hop) */
        depth = s3d_maxf(0, depth - 0.002f);
        /* a character on ground it can stand on: straight up, so that it does not slide down the slope */
        if (A->character && n.y >= A->slopeCos) push = s3d_v3(0, depth / n.y, 0);
        else push = s3d_scale(n, depth);
        A->c = s3d_add(A->c, s3d_scale(push, ia / sum));
        if (ib > 0) B->c = s3d_sub(B->c, s3d_scale(n, depth * ib / sum));
    }
    bounce = s3d_maxf(A->bounce, B->bounce);
    fric = sqrtf(A->friction * B->friction);
    if (A->character) fric = 0;   /* (a character's sideways velocity is its walk) */
    for (k = 0; k < m; k++) {
        S3dVec3 rA = s3d_sub(c[k].p, A->c), rB = s3d_sub(c[k].p, B->c), rel = s3d_sub(s3d_velAt(A, rA), s3d_velAt(B, rB)), vt;
        float vn = s3d_dot(rel, n), kn, j, vtl, kt;
        int at = slot + k < S3D_ACC && acc ? slot + k : -1;
        kn = s3d_kAt(A, rA, n) + s3d_kAt(B, rB, n);
        if (kn <= 0) continue;
        if (c[k].depth < 0) j = dt > 0 ? -(vn - c[k].depth / dt) / kn : 0;   /* (a point still apart: it may come closer, up to touching) */
        else j = -(vn + (vn < -0.5f ? bounce * vn : 0)) / kn;   /* (slow: it comes to rest) */
        if (turning && c[k].depth > 0.005f && dt > 0) j += 0.2f / dt * (c[k].depth - 0.005f) / kn;   /* (out of the overlap: 1/5 of it a step) */
        if (at >= 0) { float sumJ = s3d_maxf(acc->jn[at] + j, 0); j = sumJ - acc->jn[at]; acc->jn[at] = sumJ; }
        else if (j < 0) j = 0;
        if (j != 0) { s3d_impulse(A, rA, s3d_scale(n, j)); s3d_impulse(B, rB, s3d_scale(n, -j)); }
        if (fric <= 0) continue;
        rel = s3d_sub(s3d_velAt(A, rA), s3d_velAt(B, rB));
        vt = s3d_sub(rel, s3d_scale(n, s3d_dot(rel, n)));
        vtl = s3d_length(vt);
        if (vtl < 1e-7f) continue;
        kt = s3d_kAt(A, rA, s3d_scale(vt, 1 / vtl)) + s3d_kAt(B, rB, s3d_scale(vt, 1 / vtl));
        if (kt <= 0) continue;
        if (at >= 0) {   /* the friction of the step so far plus what stops the sliding, at most friction times the push along n */
            S3dVec3 sumT = s3d_sub(acc->jt[at], s3d_scale(vt, 1 / kt)), dj;
            float lim = fric * acc->jn[at], l = s3d_length(sumT);
            if (l > lim && l > 0) sumT = s3d_scale(sumT, lim / l);
            dj = s3d_sub(sumT, acc->jt[at]); acc->jt[at] = sumT;
            s3d_impulse(A, rA, dj); s3d_impulse(B, rB, s3d_scale(dj, -1));
        } else {
            float jt = s3d_minf(vtl / kt, fric * j);
            S3dVec3 t = s3d_scale(vt, 1 / vtl);
            s3d_impulse(A, rA, s3d_scale(t, -jt)); s3d_impulse(B, rB, s3d_scale(t, jt));
        }
    }
}

/* the contacts of A with the triangles of B, collected in groups (one triangle each) */
typedef struct S3dHitList { const S3dShape* shape; S3dContact c[32]; int start[16], count[16], n, cn; } S3dHitList;
static int s3d_collectHit(void* ctx, S3dVec3 a, S3dVec3 b, S3dVec3 c, int twoSided) {
    S3dHitList* L = (S3dHitList*)ctx;
    S3dContact k[4];
    int m, i;
    if (L->n >= 16 || L->cn + 4 > 32) return 0;
    m = s3d_contactTriangle(L->shape, a, b, c, twoSided, k);
    if (m == 0) return 0;
    L->start[L->n] = L->cn; L->count[L->n] = m; L->n++;
    for (i = 0; i < m; i++) L->c[L->cn++] = k[i];
    return 0;
}

/* The pair A (dynamic) and B: their contacts answered. test: only whether A overlaps B solidly (1). Returns whether they touched. */
static int s3d_pair(S3dBody* A, S3dBody* B, int test, S3dAcc* acc, float dt) {
    S3dShape sa = s3d_bodyShape(A), sb;
    S3dContact c[S3D_MANIFOLD];
    int m, g, k;
    if (B->shape == S3D_SHAPE_MESH) {
        S3dHitList L;
        L.shape = &sa; L.n = 0; L.cn = 0;
        s3d_staticVisit(B, sa.lo, sa.hi, s3d_collectHit, &L);
        if (L.n == 0) return 0;
        if (A->trigger || B->trigger) { s3d_touch(A, B->id); s3d_touch(B, A->id); return 0; }
        if (test) { for (k = 0; k < L.cn; k++) if (L.c[k].depth > 0.002f) return 1; return 0; }
        s3d_touch(A, B->id);
        /* the deepest triangle first; each one again after the others moved A */
        for (g = 0; g < L.n; g++) {
            int best = g, q, s, cnt;
            float depth;
            for (q = g + 1; q < L.n; q++) if (L.c[L.start[q]].depth > L.c[L.start[best]].depth) best = q;
            s = L.start[best]; L.start[best] = L.start[g]; L.start[g] = s;
            cnt = L.count[best]; L.count[best] = L.count[g]; L.count[g] = cnt;
            /* how deep it still is: along n, what A moved since */
            depth = s3d_dot(s3d_sub(A->c, sa.c), L.c[s].n);
            for (k = 0; k < cnt; k++) { c[k] = L.c[s + k]; c[k].depth -= depth; c[k].p = s3d_add(c[k].p, s3d_sub(A->c, sa.c)); }
            s3d_resolve(A, B, c, cnt, acc, g * 4, dt);
        }
        return 1;
    }
    sb = s3d_bodyShape(B);
    if (!s3d_boxesMeet(sa.lo, sa.hi, sb.lo, sb.hi) || (m = s3d_collide(&sa, &sb, c)) == 0) return 0;
    if (A->trigger || B->trigger) { s3d_touch(A, B->id); s3d_touch(B, A->id); return 0; }
    if (test) return c[0].depth > 0.002f;
    s3d_touch(A, B->id); s3d_touch(B, A->id);
    if (B->asleep) s3d_wake(B);
    s3d_resolve(A, B, c, m, acc, 0, dt);
    return 1;
}
/* whether body i overlaps any other body solidly */
static int s3d_overlapsAny(S3dBody** bodies, int n, int i) {
    int k;
    for (k = 0; k < n; k++) if (k != i && s3d_pair(bodies[i], bodies[k], 1, NULL, 0)) return 1;
    return 0;
}

typedef struct S3dRayHit { float dist; S3dVec3 point, normal; int index; } S3dRayHit;
static int s3d_raycast(S3dBody** bodies, int n, S3dVec3 o, S3dVec3 d, float maxDist, int skip, S3dRayHit* hit);

/* a joint: positions and velocities along it (rope and rod), or its force (a spring, once a step) */
static void s3d_joint(S3dJoint* j, float dt, int spring) {
    S3dBody* A = j->a;
    S3dBody* B = j->b;
    S3dVec3 ra = s3d_rot(A->R, j->la), rb = B ? s3d_rot(B->R, j->lb) : s3d_v3(0, 0, 0);
    S3dVec3 pa = s3d_add(A->c, ra), pb = B ? s3d_add(B->c, rb) : j->lb, d = s3d_sub(pb, pa), n, vrel;
    float dist = s3d_length(d), C, vn, ia, ib, sum;
    if (dist < 1e-6f) return;
    n = s3d_scale(d, 1 / dist);
    C = dist - j->length;
    vrel = B ? s3d_sub(s3d_velAt(B, rb), s3d_velAt(A, ra)) : s3d_scale(s3d_velAt(A, ra), -1);
    vn = s3d_dot(vrel, n);   /* > 0: they move apart */
    if ((j->kind == S3D_JOINT_SPRING) != spring) return;
    if (spring) {
        S3dVec3 J = s3d_scale(n, (j->stiffness * C + j->damping * vn) * dt);
        s3d_impulse(A, ra, J);
        if (B) s3d_impulse(B, rb, s3d_scale(J, -1));
        if (fabsf(C) > 0.001f) { s3d_wake(A); if (B) s3d_wake(B); }
        return;
    }
    if (j->kind == S3D_JOINT_ROPE && C <= 0) return;   /* (a rope that hangs loose) */
    ia = s3d_dynamic(A) ? A->invMass : 0; ib = B && s3d_dynamic(B) ? B->invMass : 0; sum = ia + ib;
    if (sum <= 0) return;
    if (fabsf(C) > 0.001f) { s3d_wake(A); if (B) s3d_wake(B); }
    A->c = s3d_add(A->c, s3d_scale(n, C * ia / sum));
    if (B && ib > 0) B->c = s3d_sub(B->c, s3d_scale(n, C * ib / sum));
    if (j->kind == S3D_JOINT_ROPE && vn <= 0) return;
    {
        float k = s3d_kAt(A, ra, n) + (B ? s3d_kAt(B, rb, n) : 0);
        if (k <= 0) return;
        s3d_impulse(A, ra, s3d_scale(n, vn / k));
        if (B) s3d_impulse(B, rb, s3d_scale(n, -vn / k));
    }
}

/* One step of dt seconds for the bodies (gravity: the acceleration of the world, e.g. 0, -9.81, 0) and their joints. */
static void s3d_physicsStep(S3dBody** bodies, int n, S3dJoint* joints, int jn, S3dVec3 gravity, float dt) {
    int i, k, it, pn = 0, *order, *pairs;
    S3dAcc* accs;
    S3dVec3* lo;
    S3dVec3* hi;
    /* the velocities first: gravity, damping, the walk - then the contacts answer them, and only then the bodies move (semi-implicit) */
    for (i = 0; i < n; i++) {
        S3dBody* b = bodies[i];
        b->touchN = 0;
        if (b->kind != S3D_BODY_DYNAMIC || b->asleep) continue;
        b->wasOnGround = b->onGround;
        b->onGround = 0;
        b->vel = s3d_add(b->vel, s3d_scale(gravity, b->gravity * dt));
        if (b->damping > 0) b->vel = s3d_scale(b->vel, s3d_maxf(0, 1 - b->damping * dt));
        if (b->rotates) b->w = s3d_scale(b->w, s3d_maxf(0, 1 - b->angularDamping * dt));
        if (b->character) {
            b->vel.x = b->walkX; b->vel.z = b->walkZ;
            if (b->jump > 0) { if (b->wasOnGround) b->vel.y = b->jump; b->jump = 0; }
        }
    }
    for (k = 0; k < jn; k++) s3d_joint(&joints[k], dt, 1);
    /* the pairs: the boxes sorted along x, those that overlap and where one is an awake dynamic body (or a sleeping one meets a moving kinematic one) */
    order = (int*)S3D_MALLOC(sizeof(int) * (s3d_size)(n + 1));
    lo = (S3dVec3*)S3D_MALLOC(sizeof(S3dVec3) * (s3d_size)(n + 1));
    hi = (S3dVec3*)S3D_MALLOC(sizeof(S3dVec3) * (s3d_size)(n + 1));
    pairs = NULL;
    if (order && lo && hi) {
        s3d_size cap = 0;
        for (i = 0; i < n; i++) {
            int at = i;
            s3d_bodyBox(bodies[i], &lo[i], &hi[i]);
            {   /* (grown by 5 cm and by what it moves in this step) */
                S3dVec3 v = bodies[i]->kind == S3D_BODY_KINEMATIC ? bodies[i]->vel : bodies[i]->kind == S3D_BODY_DYNAMIC ? bodies[i]->vel : s3d_v3(0, 0, 0);
                lo[i] = s3d_sub(lo[i], s3d_v3(0.05f + fabsf(v.x) * dt, 0.05f + fabsf(v.y) * dt, 0.05f + fabsf(v.z) * dt));
                hi[i] = s3d_add(hi[i], s3d_v3(0.05f + fabsf(v.x) * dt, 0.05f + fabsf(v.y) * dt, 0.05f + fabsf(v.z) * dt));
            }
            while (at > 0 && lo[order[at - 1]].x > lo[i].x) { order[at] = order[at - 1]; at--; }
            order[at] = i;
        }
        for (i = 0; i < n; i++) {
            int a = order[i];
            for (k = i + 1; k < n; k++) {
                int b = order[k], A, B, ok;
                S3dBody* ba;
                S3dBody* bb;
                if (lo[b].x > hi[a].x) break;
                if (!s3d_boxesMeet(lo[a], hi[a], lo[b], hi[b])) continue;
                ba = bodies[a]; bb = bodies[b];
                ok = (s3d_dynamic(ba) && !ba->asleep) || (s3d_dynamic(bb) && !bb->asleep)
                  || (s3d_dynamic(ba) && bb->kind == S3D_BODY_KINEMATIC) || (s3d_dynamic(bb) && ba->kind == S3D_BODY_KINEMATIC);
                if (!ok) continue;
                /* A: the dynamic one (awake first) */
                if (s3d_dynamic(ba) && (!ba->asleep || !s3d_dynamic(bb) || bb->asleep)) { A = a; B = b; } else { A = b; B = a; }
                if (!S3D_GROW(pairs, cap, (s3d_size)(pn + 1) * 2)) break;
                pairs[pn * 2] = A; pairs[pn * 2 + 1] = B; pn++;
            }
        }
    }
    accs = pn > 0 ? (S3dAcc*)s3d_calloc((s3d_size)pn, sizeof(S3dAcc)) : NULL;
    for (it = 0; it < 6; it++) {
        for (i = 0; i < n; i++) {
            S3dBody* b = bodies[i];
            if (b->character && it == 0 && b->wasOnGround && (b->walkX != 0 || b->walkZ != 0)) {
                /* a step: blocked sideways where the body is a step higher free - it goes up, and then down onto the step */
                S3dVec3 at = b->c;
                b->c.y += b->stepHeight;
                if (!s3d_overlapsAny(bodies, n, i)) {
                    S3dRayHit hit;
                    S3dShape s = s3d_bodyShape(b);
                    float foot = s.a.y - s.r;
                    if (s3d_raycast(bodies, n, s3d_v3(s.a.x, foot + 0.01f, s.a.z), s3d_v3(0, -1, 0), b->stepHeight + 0.02f, i, &hit) >= 0 && hit.normal.y >= b->slopeCos) {
                        b->c.y -= s3d_maxf(0, hit.dist - 0.008f);   /* (2 mm into the step: it stands on it) */
                        if (s3d_overlapsAny(bodies, n, i)) b->c = at;
                        else { b->onGround = 1; if (b->vel.y < 0) b->vel.y = 0; s3d_touch(b, bodies[hit.index]->id); }
                    } else b->c = at;   /* (nothing to stand on below: no step, it may fall) */
                } else b->c = at;
            }
        }
        for (k = 0; k < pn; k++) {
            S3dBody* A = bodies[pairs[k * 2]];
            S3dBody* B = bodies[pairs[k * 2 + 1]];
            if (A->asleep) {   /* a sleeping body meets a kinematic one: woken when they touch */
                if (B->kind != S3D_BODY_KINEMATIC || !s3d_pair(A, B, 1, NULL, 0)) continue;
                s3d_wake(A);
            }
            s3d_pair(A, B, 0, accs ? &accs[k] : NULL, dt);
        }
        for (k = 0; k < jn; k++) s3d_joint(&joints[k], dt, 0);
    }
    S3D_FREE(order); S3D_FREE(lo); S3D_FREE(hi); S3D_FREE(pairs); S3D_FREE(accs);
    /* now they move and turn */
    for (i = 0; i < n; i++) {
        S3dBody* b = bodies[i];
        if (b->kind == S3D_BODY_KINEMATIC) { b->c = s3d_add(b->c, s3d_scale(b->vel, dt)); continue; }
        if (b->kind != S3D_BODY_DYNAMIC || b->asleep) continue;
        b->c = s3d_add(b->c, s3d_scale(b->vel, dt));
        if (b->rotates) {   /* q += 1/2 (0, w) q dt */
            float wq[4], dq[4];
            wq[0] = 0; wq[1] = b->w.x; wq[2] = b->w.y; wq[3] = b->w.z;
            s3d_qMul(wq, b->q, dq);
            for (k = 0; k < 4; k++) b->q[k] += dq[k] * 0.5f * dt;
            s3d_qNormalize(b->q);
            s3d_qMatrix(b->q, b->R);
        }
    }
    /* resting for half a second: asleep (not characters: they wait for their walk) */
    for (i = 0; i < n; i++) {
        S3dBody* b = bodies[i];
        if (!s3d_dynamic(b) || b->asleep || b->character) continue;
        if (s3d_dot(b->vel, b->vel) < 0.0025f && s3d_dot(b->w, b->w) < 0.0025f) b->sleepTime += dt; else b->sleepTime = 0;
        if (b->sleepTime > 0.5f) { b->asleep = 1; b->vel = b->w = s3d_v3(0, 0, 0); }
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
/* a ray against a box in its own space (slabs): the distance or -1, the normal of the face (in the world) */
static float s3d_rayBox(const S3dShape* s, S3dVec3 o, S3dVec3 d, S3dVec3* n) {
    S3dVec3 lo = s3d_toBox(s, o), ld = s3d_v3(s3d_dot(d, s->ax[0]), s3d_dot(d, s->ax[1]), s3d_dot(d, s->ax[2]));
    float tmin = -1e30f, tmax = 1e30f, oo[3], dd[3], h[3];
    int k, axis = 1;
    oo[0] = lo.x; oo[1] = lo.y; oo[2] = lo.z; dd[0] = ld.x; dd[1] = ld.y; dd[2] = ld.z; h[0] = s->h.x; h[1] = s->h.y; h[2] = s->h.z;
    for (k = 0; k < 3; k++) {
        if (fabsf(dd[k]) < 1e-12f) { if (oo[k] < -h[k] || oo[k] > h[k]) return -1; continue; }
        {
            float t1 = (-h[k] - oo[k]) / dd[k], t2 = (h[k] - oo[k]) / dd[k];
            if (t1 > t2) { float x = t1; t1 = t2; t2 = x; }
            if (t1 > tmin) { tmin = t1; axis = k; }
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return -1;
        }
    }
    if (tmax < 0) return -1;
    if (tmin < 0) { *n = s3d_scale(d, -1); return 0; }   /* (inside) */
    *n = s3d_scale(s->ax[axis], dd[axis] > 0 ? -1.0f : 1.0f);
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
            S3dShape s = s3d_bodyShape(b);
            if (!s3d_boxesMeet(rlo, rhi, s.lo, s.hi)) continue;
            if (s.box) t = s3d_rayBox(&s, o, d, &nn);
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
