/* fire scene3d: physics (scene3d.physics, docs/graphics/physics.md) - simple, small and the same everywhere.
 *
 * Bodies: static (the ground, walls: they never move), dynamic (they fall, bounce, tumble, are pushed) and kinematic (moved by the program or by their
 * velocity, they push dynamic bodies and are not pushed). Shapes: a box, a sphere, an upright capsule (figures) - and for static bodies the triangles of
 * the object itself (a mesh, in an xz grid so that only the near ones are tested) or the heights of a terrain (no triangles in memory: those of the cells
 * below a body are made when they are needed).
 * Dynamic boxes and spheres turn (an orientation and an angular velocity; boxes tumble, balls roll); contacts have points (up to 8 a pair: a box lies
 * on its four corners), impulses act at the points. A step: the velocities (gravity, damping, the walk), the springs, then the pairs that the sorted
 * boxes find are answered six times (the impulses of a step summed point by point: never pulling, the friction at most its share), the ropes and rods -
 * and only then the bodies move and turn. A body that rests half a second sleeps until something touches it. A character is an upright capsule that
 * walks (its sideways velocity is set), jumps, climbs steps up to a height and stands on slopes up to an angle.
 * Everything is float and deterministic: the same steps give the same positions in the VM and in a native build.
 * (Vectors go by pointer: on 16 bits a vector of 12 bytes handed by value costs a copy at every call - about a third of this code.) */
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
/* a contact: n the way the first shape is pushed out, p the point (in the world), depth how far they overlap (below 0: still apart) */
typedef struct S3dContact { S3dVec3 n, p; float depth; } S3dContact;

/* ---- vectors by pointer (r may be one of the others) ---- */
static void s3d_vset(S3dVec3* r, float x, float y, float z) { r->x = x; r->y = y; r->z = z; }
static void s3d_vadd(S3dVec3* r, const S3dVec3* a, const S3dVec3* b) { r->x = a->x + b->x; r->y = a->y + b->y; r->z = a->z + b->z; }
static void s3d_vsub(S3dVec3* r, const S3dVec3* a, const S3dVec3* b) { r->x = a->x - b->x; r->y = a->y - b->y; r->z = a->z - b->z; }
/* r = a + b * s */
static void s3d_vmad(S3dVec3* r, const S3dVec3* a, const S3dVec3* b, float s) { r->x = a->x + b->x * s; r->y = a->y + b->y * s; r->z = a->z + b->z * s; }
static void s3d_vscale(S3dVec3* r, const S3dVec3* a, float s) { r->x = a->x * s; r->y = a->y * s; r->z = a->z * s; }
static float s3d_vdot(const S3dVec3* a, const S3dVec3* b) { return a->x * b->x + a->y * b->y + a->z * b->z; }
static void s3d_vcross(S3dVec3* r, const S3dVec3* a, const S3dVec3* b) {
    float x = a->y * b->z - a->z * b->y, y = a->z * b->x - a->x * b->z, z = a->x * b->y - a->y * b->x;
    r->x = x; r->y = y; r->z = z;
}
static float s3d_vlen(const S3dVec3* a) { return sqrtf(s3d_vdot(a, a)); }
/* (a - b) . n */
static float s3d_vsubDot(const S3dVec3* a, const S3dVec3* b, const S3dVec3* n) { return (a->x - b->x) * n->x + (a->y - b->y) * n->y + (a->z - b->z) * n->z; }
static float s3d_vdist2(const S3dVec3* a, const S3dVec3* b) { float x = a->x - b->x, y = a->y - b->y, z = a->z - b->z; return x * x + y * y + z * z; }
/* r = a / |a| (no length: up); returns the length */
static float s3d_vnorm(S3dVec3* r, const S3dVec3* a) {
    float l = s3d_vlen(a);
    if (l > 1e-20f) s3d_vscale(r, a, 1 / l); else s3d_vset(r, 0, 1, 0);
    return l;
}
static void s3d_vmin(S3dVec3* r, const S3dVec3* a) { if (a->x < r->x) r->x = a->x; if (a->y < r->y) r->y = a->y; if (a->z < r->z) r->z = a->z; }
static void s3d_vmax(S3dVec3* r, const S3dVec3* a) { if (a->x > r->x) r->x = a->x; if (a->y > r->y) r->y = a->y; if (a->z > r->z) r->z = a->z; }
/* r = R v, r = R^T v */
static void s3d_vrot(S3dVec3* r, const float* R, const S3dVec3* v) {
    float x = R[0] * v->x + R[1] * v->y + R[2] * v->z, y = R[3] * v->x + R[4] * v->y + R[5] * v->z, z = R[6] * v->x + R[7] * v->y + R[8] * v->z;
    r->x = x; r->y = y; r->z = z;
}
static void s3d_vrotT(S3dVec3* r, const float* R, const S3dVec3* v) {
    float x = R[0] * v->x + R[3] * v->y + R[6] * v->z, y = R[1] * v->x + R[4] * v->y + R[7] * v->z, z = R[2] * v->x + R[5] * v->y + R[8] * v->z;
    r->x = x; r->y = y; r->z = z;
}
static void s3d_vpoint(S3dVec3* r, const S3dMat4* m, const S3dVec3* p) {
    float x = S3D_AT(*m, 0, 0) * p->x + S3D_AT(*m, 0, 1) * p->y + S3D_AT(*m, 0, 2) * p->z + S3D_AT(*m, 0, 3);
    float y = S3D_AT(*m, 1, 0) * p->x + S3D_AT(*m, 1, 1) * p->y + S3D_AT(*m, 1, 2) * p->z + S3D_AT(*m, 1, 3);
    float z = S3D_AT(*m, 2, 0) * p->x + S3D_AT(*m, 2, 1) * p->y + S3D_AT(*m, 2, 2) * p->z + S3D_AT(*m, 2, 3);
    r->x = x; r->y = y; r->z = z;
}

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
/* the body turned by a small angle (the vector: axis times angle) */
static void s3d_turnBy(S3dBody* b, const S3dVec3* a, float dt) {
    float wq[4], dq[4];
    int k;
    wq[0] = 0; wq[1] = a->x; wq[2] = a->y; wq[3] = a->z;
    s3d_qMul(wq, b->q, dq);
    for (k = 0; k < 4; k++) b->q[k] += dq[k] * 0.5f * dt;
    s3d_qNormalize(b->q);
    s3d_qMatrix(b->q, b->R);
}

static S3dBody* s3d_bodyNew(void) {
    S3dBody* b = (S3dBody*)s3d_calloc(1, sizeof(S3dBody));
    if (b == NULL) return NULL;
    b->invMass = 1; b->bounce = 0.1f; b->friction = 0.5f; b->gravity = 1; b->stepHeight = 0.3f; b->slopeCos = 0.7071f; b->angularDamping = 0.3f;
    b->q[0] = 1; s3d_qMatrix(b->q, b->R);
    return b;
}
static void s3d_soupFree(S3dSoup* s) { if (s) { S3D_FREE(s->v); S3D_FREE(s->t); S3D_FREE(s->start); S3D_FREE(s->list); S3D_FREE(s); } }
static void s3d_bodyFree(S3dBody* b) { if (b) { s3d_soupFree(b->soup); S3D_FREE(b); } }
static int s3d_dynamic(const S3dBody* b) { return b->kind == S3D_BODY_DYNAMIC; }
static void s3d_wake(S3dBody* b) { b->asleep = 0; b->sleepTime = 0; }

/* the moments of inertia from the shape and the mass (a box: m (b^2 + c^2) / 3 with half sizes; a sphere: 2/5 m r^2) */
static void s3d_bodyInertia(S3dBody* b) {
    float m = b->invMass > 0 ? 1 / b->invMass : 0, hx = b->half.x * b->half.x, hy = b->half.y * b->half.y, hz = b->half.z * b->half.z, i[3];
    int k;
    s3d_vset(&b->invI, 0, 0, 0);
    if (!b->rotates || m <= 0 || !s3d_dynamic(b)) return;
    if (b->shape == S3D_SHAPE_SPHERE) i[0] = i[1] = i[2] = 0.4f * m * hx;
    else if (b->shape == S3D_SHAPE_BOX) { i[0] = m * (hy + hz) / 3; i[1] = m * (hx + hz) / 3; i[2] = m * (hx + hy) / 3; }
    else return;
    for (k = 0; k < 3; k++) i[k] = i[k] > 1e-9f ? 1 / i[k] : 0;
    s3d_vset(&b->invI, i[0], i[1], i[2]);
}

/* ---- shapes ---- */
static float s3d_boxR(const S3dShape* s, const S3dVec3* n) { return s->h.x * fabsf(s3d_vdot(&s->ax[0], n)) + s->h.y * fabsf(s3d_vdot(&s->ax[1], n)) + s->h.z * fabsf(s3d_vdot(&s->ax[2], n)); }
/* a point in the box's own space, and back */
static void s3d_toBox(S3dVec3* r, const S3dShape* s, const S3dVec3* p) {
    S3dVec3 d;
    s3d_vsub(&d, p, &s->c);
    s3d_vset(r, s3d_vdot(&d, &s->ax[0]), s3d_vdot(&d, &s->ax[1]), s3d_vdot(&d, &s->ax[2]));
}
static void s3d_fromBox(S3dVec3* r, const S3dShape* s, float x, float y, float z) {
    S3dVec3 t;
    s3d_vmad(&t, &s->c, &s->ax[0], x); s3d_vmad(&t, &t, &s->ax[1], y); s3d_vmad(r, &t, &s->ax[2], z);
}
static void s3d_boxCorners(const S3dShape* s, S3dVec3* v) {
    int k;
    for (k = 0; k < 8; k++) s3d_fromBox(&v[k], s, (k & 1) ? s->h.x : -s->h.x, (k & 2) ? s->h.y : -s->h.y, (k & 4) ? s->h.z : -s->h.z);
}
static void s3d_bodyShape(S3dShape* s, const S3dBody* b) {
    memset(s, 0, sizeof *s);
    s->c = b->c;
    if (b->shape == S3D_SHAPE_BOX || b->shape == S3D_SHAPE_MESH) {
        S3dVec3 e, unit;
        int k;
        s->box = 1; s->h = b->half;
        for (k = 0; k < 3; k++) s3d_vset(&s->ax[k], b->R[k], b->R[3 + k], b->R[6 + k]);
        s3d_vset(&unit, 1, 0, 0); e.x = s3d_boxR(s, &unit);
        s3d_vset(&unit, 0, 1, 0); e.y = s3d_boxR(s, &unit);
        s3d_vset(&unit, 0, 0, 1); e.z = s3d_boxR(s, &unit);
        s3d_vsub(&s->lo, &s->c, &e); s3d_vadd(&s->hi, &s->c, &e); s->a = s->b = s->c;
        return;
    }
    s->r = b->half.x;
    s->a = s->b = s->c;
    if (b->shape == S3D_SHAPE_CAPSULE) { s->a.y -= b->half.y; s->b.y += b->half.y; }
    s3d_vset(&s->lo, s->a.x - s->r, s->a.y - s->r, s->a.z - s->r);
    s3d_vset(&s->hi, s->b.x + s->r, s->b.y + s->r, s->b.z + s->r);
}

/* ---- closest points ---- */
static float s3d_clamp01(float t) { return t < 0 ? 0 : t > 1 ? 1 : t; }
static void s3d_onSegment(S3dVec3* r, const S3dVec3* a, const S3dVec3* b, const S3dVec3* p) {
    S3dVec3 d;
    float dd;
    s3d_vsub(&d, b, a);
    dd = s3d_vdot(&d, &d);
    if (dd < 1e-12f) { *r = *a; return; }
    s3d_vmad(r, a, &d, s3d_clamp01(s3d_vsubDot(p, a, &d) / dd));
}
static void s3d_inBox(S3dVec3* r, const S3dVec3* p, const S3dVec3* h) {
    s3d_vset(r, p->x < -h->x ? -h->x : p->x > h->x ? h->x : p->x, p->y < -h->y ? -h->y : p->y > h->y ? h->y : p->y, p->z < -h->z ? -h->z : p->z > h->z ? h->z : p->z);
}
/* the closest points of the segments p1..q1 and p2..q2 (Ericson, Real-Time Collision Detection 5.1.9) */
static void s3d_segSeg(const S3dVec3* p1, const S3dVec3* q1, const S3dVec3* p2, const S3dVec3* q2, S3dVec3* c1, S3dVec3* c2) {
    S3dVec3 d1, d2, r;
    float a, e, f, s, t;
    s3d_vsub(&d1, q1, p1); s3d_vsub(&d2, q2, p2); s3d_vsub(&r, p1, p2);
    a = s3d_vdot(&d1, &d1); e = s3d_vdot(&d2, &d2); f = s3d_vdot(&d2, &r);
    if (a < 1e-12f && e < 1e-12f) { *c1 = *p1; *c2 = *p2; return; }
    if (a < 1e-12f) { s = 0; t = s3d_clamp01(f / e); }
    else {
        float c = s3d_vdot(&d1, &r);
        if (e < 1e-12f) { t = 0; s = s3d_clamp01(-c / a); }
        else {
            float b = s3d_vdot(&d1, &d2), den = a * e - b * b;
            s = den > 1e-12f ? s3d_clamp01((b * f - c * e) / den) : 0;
            t = (b * s + f) / e;
            if (t < 0) { t = 0; s = s3d_clamp01(-c / a); }
            else if (t > 1) { t = 1; s = s3d_clamp01((b - c) / a); }
        }
    }
    s3d_vmad(c1, p1, &d1, s); s3d_vmad(c2, p2, &d2, t);
}
/* the closest point of the triangle a, b, c to p (Ericson 5.1.5) */
static void s3d_onTriangle(S3dVec3* r, const S3dVec3* p, const S3dVec3* a, const S3dVec3* b, const S3dVec3* c) {
    S3dVec3 ab, ac, bc;
    float d1, d2, d3, d4, d5, d6, va, vb, vc, den;
    s3d_vsub(&ab, b, a); s3d_vsub(&ac, c, a);
    d1 = s3d_vsubDot(p, a, &ab); d2 = s3d_vsubDot(p, a, &ac);
    if (d1 <= 0 && d2 <= 0) { *r = *a; return; }
    d3 = s3d_vsubDot(p, b, &ab); d4 = s3d_vsubDot(p, b, &ac);
    if (d3 >= 0 && d4 <= d3) { *r = *b; return; }
    vc = d1 * d4 - d3 * d2;
    if (vc <= 0 && d1 >= 0 && d3 <= 0) { s3d_vmad(r, a, &ab, d1 / (d1 - d3)); return; }
    d5 = s3d_vsubDot(p, c, &ab); d6 = s3d_vsubDot(p, c, &ac);
    if (d6 >= 0 && d5 <= d6) { *r = *c; return; }
    vb = d5 * d2 - d1 * d6;
    if (vb <= 0 && d2 >= 0 && d6 <= 0) { s3d_vmad(r, a, &ac, d2 / (d2 - d6)); return; }
    va = d3 * d6 - d5 * d4;
    if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { s3d_vsub(&bc, c, b); s3d_vmad(r, b, &bc, (d4 - d3) / ((d4 - d3) + (d5 - d6))); return; }
    den = 1 / (va + vb + vc);
    s3d_vmad(r, a, &ab, vb * den); s3d_vmad(r, r, &ac, vc * den);
}
/* the closest points of the segment p..q and the triangle (on the segment *ps, on the triangle *pt) */
static void s3d_segTriangle(const S3dVec3* p, const S3dVec3* q, const S3dVec3* tri, const S3dVec3* n, S3dVec3* ps, S3dVec3* pt) {
    S3dVec3 cs, ct, d;
    float best, dist, dn;
    int k;
    s3d_vsub(&d, q, p);
    dn = s3d_vdot(&d, n);
    /* the segment through the triangle */
    if (dn * dn > 1e-12f) {
        float t = s3d_vsubDot(&tri[0], p, n) / dn;
        if (t >= 0 && t <= 1) {
            S3dVec3 x, on;
            s3d_vmad(&x, p, &d, t);
            s3d_onTriangle(&on, &x, &tri[0], &tri[1], &tri[2]);
            if (s3d_vdist2(&on, &x) < 1e-10f) { *ps = x; *pt = x; return; }
        }
    }
    *ps = *p; s3d_onTriangle(pt, p, &tri[0], &tri[1], &tri[2]);
    best = s3d_vdist2(ps, pt);
    if (s3d_vdot(&d, &d) < 1e-12f) return;
    s3d_onTriangle(&ct, q, &tri[0], &tri[1], &tri[2]);
    dist = s3d_vdist2(q, &ct);
    if (dist < best) { best = dist; *ps = *q; *pt = ct; }
    for (k = 0; k < 3; k++) {
        s3d_segSeg(p, q, &tri[k], &tri[(k + 1) % 3], &cs, &ct);
        dist = s3d_vdist2(&cs, &ct);
        if (dist < best) { best = dist; *ps = cs; *pt = ct; }
    }
}

/* ---- contacts: each function fills up to S3D_MANIFOLD contacts and returns how many ---- */
static int s3d_inBoxShape(const S3dShape* s, const S3dVec3* p, float margin) {
    S3dVec3 l;
    s3d_toBox(&l, s, p);
    return fabsf(l.x) <= s->h.x + margin && fabsf(l.y) <= s->h.y + margin && fabsf(l.z) <= s->h.z + margin;
}
/* a polygon (up to 8 points) cut by the plane dot(p, n) * sign <= d */
static int s3d_clipPoly(const S3dVec3* in, int count, const S3dVec3* n, float sign, float d, S3dVec3* out) {
    int k, m = 0;
    for (k = 0; k < count; k++) {
        const S3dVec3* p = &in[k];
        const S3dVec3* q = &in[(k + 1) % count];
        float sp = s3d_vdot(p, n) * sign - d, sq = s3d_vdot(q, n) * sign - d;
        if (sp <= 0 && m < 8) out[m++] = *p;
        if ((sp < 0) != (sq < 0) && m < 8) { S3dVec3 e; s3d_vsub(&e, q, p); s3d_vmad(&out[m++], p, &e, sp / (sp - sq)); }
    }
    return m;
}
static void s3d_contact(S3dContact* c, const S3dVec3* n, const S3dVec3* p, float depth) { c->n = *n; c->p = *p; c->depth = depth; }
/* a box against a box: the 15 separating axes; on a face the other box's face that looks at it is cut by the face's sides - its corners up to 2 cm
 * apart are the points (a box lying on a box: four points, it stays flat); an edge on an edge: one point between them */
static int s3d_boxBox(const S3dShape* A, const S3dShape* B, S3dContact* out) {
    S3dVec3 d, n, L;
    float best = 1e30f, depth = 0;
    int k, m = 0, axis = -1;
    s3d_vsub(&d, &A->c, &B->c); s3d_vset(&n, 0, 1, 0);
    for (k = 0; k < 15; k++) {
        float len, ov, side, score;
        if (k < 3) L = A->ax[k]; else if (k < 6) L = B->ax[k - 3]; else s3d_vcross(&L, &A->ax[(k - 6) / 3], &B->ax[(k - 6) % 3]);
        len = s3d_vlen(&L);
        if (len < 1e-5f) continue;
        s3d_vscale(&L, &L, 1 / len);
        side = s3d_vdot(&d, &L);
        ov = s3d_boxR(A, &L) + s3d_boxR(B, &L) - fabsf(side);
        if (ov <= 0) return 0;
        /* (faces before edges; of two faces about as good the one of B - the one below, mostly - so that the normal does not flip between them) */
        score = k < 3 ? ov * 1.02f + 0.0005f : k < 6 ? ov : ov * 1.05f + 0.001f;
        if (score < best) { best = score; depth = ov; s3d_vscale(&n, &L, side < 0 ? -1.0f : 1.0f); axis = k; }
    }
    if (axis < 6) {
        const S3dShape* ref = axis < 3 ? A : B;
        const S3dShape* inc = axis < 3 ? B : A;
        S3dVec3 nr, poly[8], tmp[8], fc, ic;
        float hh[3], ih[3], hu, hv, cu, cv, s;
        int r = axis % 3, ia = 0, ru = (r + 1) % 3, rv = (r + 2) % 3, iua, iva, cnt;
        s3d_vscale(&nr, &n, axis < 3 ? -1.0f : 1.0f);   /* the reference face's normal, towards the other box */
        hh[0] = ref->h.x; hh[1] = ref->h.y; hh[2] = ref->h.z; ih[0] = inc->h.x; ih[1] = inc->h.y; ih[2] = inc->h.z;
        hu = hh[ru]; hv = hh[rv];
        s3d_vmad(&fc, &ref->c, &nr, hh[r]);
        /* the incident face: the face of the other box most against nr */
        for (k = 1; k < 3; k++) if (fabsf(s3d_vdot(&inc->ax[k], &nr)) > fabsf(s3d_vdot(&inc->ax[ia], &nr))) ia = k;
        s = s3d_vdot(&inc->ax[ia], &nr) > 0 ? -ih[ia] : ih[ia];
        s3d_vmad(&ic, &inc->c, &inc->ax[ia], s);
        iua = (ia + 1) % 3; iva = (ia + 2) % 3;
        for (k = 0; k < 4; k++) {
            s3d_vmad(&poly[k], &ic, &inc->ax[iua], (k == 0 || k == 3) ? ih[iua] : -ih[iua]);
            s3d_vmad(&poly[k], &poly[k], &inc->ax[iva], k < 2 ? ih[iva] : -ih[iva]);
        }
        cu = s3d_vdot(&ref->c, &ref->ax[ru]); cv = s3d_vdot(&ref->c, &ref->ax[rv]);
        cnt = s3d_clipPoly(poly, 4, &ref->ax[ru], 1, cu + hu, tmp);
        cnt = s3d_clipPoly(tmp, cnt, &ref->ax[ru], -1, -cu + hu, poly);
        cnt = s3d_clipPoly(poly, cnt, &ref->ax[rv], 1, cv + hv, tmp);
        cnt = s3d_clipPoly(tmp, cnt, &ref->ax[rv], -1, -cv + hv, poly);
        for (k = 0; k < cnt && m < S3D_MANIFOLD; k++) {
            float sep = s3d_vsubDot(&poly[k], &fc, &nr);
            if (sep <= 0.02f) s3d_contact(&out[m++], &n, &poly[k], s3d_minf(-sep, depth));
        }
        if (m > 0) return m;
    }
    {   /* edge on edge: between the corner of A deepest along -n and the corner of B deepest along n */
        S3dVec3 va[8], vb[8], p;
        int ka = 0, kb = 0;
        s3d_boxCorners(A, va); s3d_boxCorners(B, vb);
        for (k = 1; k < 8; k++) { if (s3d_vdot(&va[k], &n) < s3d_vdot(&va[ka], &n)) ka = k; if (s3d_vdot(&vb[k], &n) > s3d_vdot(&vb[kb], &n)) kb = k; }
        s3d_vadd(&p, &va[ka], &vb[kb]); s3d_vscale(&p, &p, 0.5f);
        s3d_contact(&out[0], &n, &p, depth);
        return 1;
    }
}
/* a sphere or capsule against a box: in the box's own space (n pushes the round one out, p on the box) */
static int s3d_roundBox(const S3dShape* R, const S3dShape* B, S3dContact* c) {
    S3dVec3 a, b, p, q, d, zero;
    float dist, f[6];
    int k, best = 0;
    s3d_toBox(&a, B, &R->a); s3d_toBox(&b, B, &R->b); s3d_vset(&zero, 0, 0, 0);
    s3d_onSegment(&p, &a, &b, &zero);
    for (k = 0; k < 3; k++) { s3d_inBox(&q, &p, &B->h); s3d_onSegment(&p, &a, &b, &q); }
    s3d_inBox(&q, &p, &B->h);
    s3d_vsub(&d, &p, &q);
    dist = s3d_vlen(&d);
    if (dist > R->r) return 0;
    if (dist > 1e-6f) { s3d_vscale(&d, &d, 1 / dist); c->depth = R->r - dist; }
    else {   /* the middle of the round one inside the box: out by the nearest face */
        f[0] = p.x + B->h.x; f[1] = B->h.x - p.x; f[2] = p.y + B->h.y; f[3] = B->h.y - p.y; f[4] = p.z + B->h.z; f[5] = B->h.z - p.z;
        for (k = 1; k < 6; k++) if (f[k] < f[best]) best = k;
        s3d_vset(&d, best == 0 ? -1.0f : best == 1 ? 1.0f : 0, best == 2 ? -1.0f : best == 3 ? 1.0f : 0, best == 4 ? -1.0f : best == 5 ? 1.0f : 0);
        c->depth = f[best] + R->r;
    }
    s3d_vscale(&c->n, &B->ax[0], d.x); s3d_vmad(&c->n, &c->n, &B->ax[1], d.y); s3d_vmad(&c->n, &c->n, &B->ax[2], d.z);
    s3d_fromBox(&c->p, B, q.x, q.y, q.z);
    return 1;
}
static int s3d_roundRound(const S3dShape* A, const S3dShape* B, S3dContact* c) {
    S3dVec3 pa, pb, d;
    float dist, rr = A->r + B->r;
    s3d_segSeg(&A->a, &A->b, &B->a, &B->b, &pa, &pb);
    s3d_vsub(&d, &pa, &pb);
    dist = s3d_vnorm(&c->n, &d);
    if (dist >= rr) return 0;
    if (dist <= 1e-6f) s3d_vset(&c->n, 0, 1, 0);
    c->depth = rr - dist;
    s3d_vmad(&c->p, &pb, &c->n, B->r);
    return 1;
}
static int s3d_collide(const S3dShape* A, const S3dShape* B, S3dContact* c) {
    if (A->box && B->box) return s3d_boxBox(A, B, c);
    if (!A->box && !B->box) return s3d_roundRound(A, B, c);
    if (!A->box) return s3d_roundBox(A, B, c);
    if (!s3d_roundBox(B, A, c)) return 0;
    s3d_vscale(&c->n, &c->n, -1);
    return 1;
}

/* p (in the plane of the triangle) inside it, with a little room */
static int s3d_inTriangle(const S3dVec3* p, const S3dVec3* tri, const S3dVec3* n) {
    int k;
    for (k = 0; k < 3; k++) {
        S3dVec3 e, f;
        s3d_vsub(&e, &tri[(k + 1) % 3], &tri[k]); s3d_vsub(&f, p, &tri[k]);
        s3d_vcross(&e, &e, &f);
        if (s3d_vdot(&e, n) < -1e-4f) return 0;
    }
    return 1;
}
/* a shape against a triangle (counter clockwise seen from its front; twoSided: both sides push out) - up to 4 contacts */
static int s3d_contactTriangle(const S3dShape* A, const S3dVec3* tri, int twoSided, S3dContact* out) {
    S3dVec3 n, e1, e2;
    float nl;
    s3d_vsub(&e1, &tri[1], &tri[0]); s3d_vsub(&e2, &tri[2], &tri[0]);
    s3d_vcross(&n, &e1, &e2);
    nl = s3d_vlen(&n);
    if (nl < 1e-12f) return 0;
    s3d_vscale(&n, &n, 1 / nl);
    if (A->box) {   /* separating axes: the box's three, the face, the nine crossings of edges and box axes */
        S3dVec3 axis, bestAxis = n, v[8], fn, e;
        float best = 1e30f, bestScore = 1e30f, er = s3d_boxR(A, &n), side = s3d_vsubDot(&A->c, &tri[0], &n), dn;
        int k, j, m = 0;
        if (!twoSided && side < -er) return 0;   /* wholly behind */
        for (k = 0; k < 13; k++) {
            float len, tmin, tmax, rb, pc, ov, t;
            if (k < 3) axis = A->ax[k];
            else if (k == 3) axis = n;
            else { s3d_vsub(&e, &tri[((k - 4) / 3 + 1) % 3], &tri[(k - 4) / 3]); s3d_vcross(&axis, &e, &A->ax[(k - 4) % 3]); }
            len = s3d_vlen(&axis);
            if (len < 1e-6f) continue;
            s3d_vscale(&axis, &axis, 1 / len);
            tmin = tmax = s3d_vdot(&tri[0], &axis);
            for (j = 1; j < 3; j++) { t = s3d_vdot(&tri[j], &axis); if (t < tmin) tmin = t; if (t > tmax) tmax = t; }
            rb = s3d_boxR(A, &axis);
            pc = s3d_vdot(&A->c, &axis);
            ov = s3d_minf(tmax - (pc - rb), (pc + rb) - tmin);
            if (ov <= 0) return 0;
            /* (the face is the better way out when it is about as good) */
            if ((k == 3 ? ov * 0.95f : ov) < bestScore) { bestScore = k == 3 ? ov * 0.95f : ov; best = ov; s3d_vscale(&bestAxis, &axis, s3d_vsubDot(&A->c, &tri[0], &axis) < 0 ? -1.0f : 1.0f); }
        }
        if (!twoSided && s3d_vdot(&bestAxis, &n) < -0.01f) { bestAxis = n; best = er - side; }
        if (best <= 0) return 0;
        /* the points: the corners of the box behind the face, inside the triangle (a box on the ground: its four lower corners) */
        s3d_boxCorners(A, v);
        dn = s3d_vdot(&bestAxis, &n);
        if (fabsf(dn) > 0.9f) {
            s3d_vscale(&fn, &n, dn > 0 ? 1.0f : -1.0f);
            for (k = 0; k < 8 && m < 4; k++) {
                float sd = s3d_vsubDot(&v[k], &tri[0], &fn);
                S3dVec3 on;
                s3d_vmad(&on, &v[k], &fn, -sd);
                if (sd < 0.02f && s3d_inTriangle(&on, tri, &n)) s3d_contact(&out[m++], &fn, &v[k], -sd);
            }
        }
        if (m == 0) {
            int lo = 0;
            for (k = 1; k < 8; k++) if (s3d_vdot(&v[k], &bestAxis) < s3d_vdot(&v[lo], &bestAxis)) lo = k;
            s3d_contact(&out[0], &bestAxis, &v[lo], best); m = 1;
        }
        return m;
    } else {
        S3dVec3 ps, pt, d;
        float dist, s;
        s3d_segTriangle(&A->a, &A->b, tri, &n, &ps, &pt);
        s3d_vsub(&d, &ps, &pt);
        dist = s3d_vlen(&d);
        if (dist >= A->r) return 0;
        s = s3d_vsubDot(&ps, &tri[0], &n);
        out->p = pt;
        if (!twoSided && dist > 1e-6f && s3d_vdot(&d, &n) < 0) {
            /* behind the face: only when the middle went just through it, inside the face (behind an edge the other faces of the body answer; deeper
             * than the radius it is the other side of a thin wall) */
            if (-s3d_vdot(&d, &n) < 0.99f * dist || -s > A->r) return 0;
            out->n = n; out->depth = A->r - s;
            return 1;
        }
        if (dist > 1e-6f) { s3d_vscale(&out->n, &d, 1 / dist); out->depth = A->r - dist; }
        else { s3d_vscale(&out->n, &n, (twoSided && s < 0) ? -1.0f : 1.0f); out->depth = A->r; }
        return 1;
    }
}

/* ---- the triangles of static bodies ---- */
static int s3d_boxesMeet(const S3dVec3* alo, const S3dVec3* ahi, const S3dVec3* blo, const S3dVec3* bhi) {
    return alo->x <= bhi->x && ahi->x >= blo->x && alo->y <= bhi->y && ahi->y >= blo->y && alo->z <= bhi->z && ahi->z >= blo->z;
}
/* the cells of a soup from a to b along x (or z) */
static int s3d_soupCell(const S3dSoup* s, float v, int z) {
    int c = (int)((v - (z ? s->lo.z : s->lo.x)) / (z ? s->cz : s->cx)), n = z ? s->gz : s->gx;
    return c < 0 ? 0 : c >= n ? n - 1 : c;
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
    s3d_vset(&s->lo, 1e30f, 1e30f, 1e30f); s3d_vset(&s->hi, -1e30f, -1e30f, -1e30f);
    for (i = 0; i < count; i++) { s3d_vmin(&s->lo, (const S3dVec3*)&v[i * 3]); s3d_vmax(&s->hi, (const S3dVec3*)&v[i * 3]); }
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
            int x0 = s3d_soupCell(s, s3d_min3f(p0[0], p1[0], p2[0]), 0), x1 = s3d_soupCell(s, s3d_max3f(p0[0], p1[0], p2[0]), 0);
            int z0 = s3d_soupCell(s, s3d_min3f(p0[2], p1[2], p2[2]), 1), z1 = s3d_soupCell(s, s3d_max3f(p0[2], p1[2], p2[2]), 1), x, z;
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
typedef int (*S3dTriVisit)(void* ctx, const S3dVec3* tri, int twoSided);

static void s3d_soupVisit(const S3dSoup* s, const S3dVec3* lo, const S3dVec3* hi, int twoSided, S3dTriVisit visit, void* ctx) {
    int x0, x1, z0, z1, x, z, j;
    s3d_size k;
    if (!s3d_boxesMeet(lo, hi, &s->lo, &s->hi)) return;
    x0 = s3d_soupCell(s, lo->x, 0); x1 = s3d_soupCell(s, hi->x, 0); z0 = s3d_soupCell(s, lo->z, 1); z1 = s3d_soupCell(s, hi->z, 1);
    for (z = z0; z <= z1; z++) for (x = x0; x <= x1; x++) {
        s3d_size cell = (s3d_size)z * (s3d_size)s->gx + (s3d_size)x;
        for (k = s->start[cell]; k < s->start[cell + 1]; k++) {
            uint32_t t = s->list[k];
            S3dVec3 tri[3], tlo, thi;
            for (j = 0; j < 3; j++) tri[j] = *(const S3dVec3*)&s->v[s->t[t * 3 + j] * 3];
            tlo = thi = tri[0];
            s3d_vmin(&tlo, &tri[1]); s3d_vmin(&tlo, &tri[2]); s3d_vmax(&thi, &tri[1]); s3d_vmax(&thi, &tri[2]);
            /* (a triangle in several cells comes once: in the first cell of it that the query covers) */
            if (s3d_maxi(x0, s3d_soupCell(s, tlo.x, 0)) != x || s3d_maxi(z0, s3d_soupCell(s, tlo.z, 1)) != z) continue;
            if (!s3d_boxesMeet(lo, hi, &tlo, &thi)) continue;
            if (visit(ctx, tri, twoSided)) return;
        }
    }
}

/* the triangles of a terrain's cells under a box of the world (made from the heights, as fire_s3d_terrainMesh makes them) */
static int s3d_heightsQuad(const S3dHeights* h, const S3dVec3* q, S3dTriVisit visit, void* ctx) {
    S3dVec3 w[4], t[3];
    int k;
    for (k = 0; k < 4; k++) s3d_vpoint(&w[k], &h->model, &q[k]);
    t[0] = w[0]; t[1] = w[1]; t[2] = w[2];
    if (visit(ctx, t, 0)) return 1;
    t[1] = w[2]; t[2] = w[3];
    return visit(ctx, t, 0);
}
/* an upright wall from (x0, z0) to (x1, z1), lo .. hi high (its front: counter clockwise) */
static int s3d_heightsWall(const S3dHeights* h, float x0, float z0, float x1, float z1, float lo, float hi, S3dTriVisit visit, void* ctx) {
    S3dVec3 q[4];
    s3d_vset(&q[0], x0, lo, z0); s3d_vset(&q[1], x1, lo, z1); s3d_vset(&q[2], x1, hi, z1); s3d_vset(&q[3], x0, hi, z0);
    return s3d_heightsQuad(h, q, visit, ctx);
}
static float s3d_heightAt(const S3dHeights* h, int i, int j) { return i < 0 || j < 0 || i >= h->cols || j >= h->rows ? h->minH : h->H[(s3d_size)j * h->cols + i]; }
static void s3d_heightsVisit(const S3dHeights* h, const S3dVec3* lo, const S3dVec3* hi, S3dTriVisit visit, void* ctx) {
    S3dVec3 llo, lhi, q[4];
    float x0w, z0w;
    int c, i, j, i0, i1, j0, j1, hard = h->hard;
    s3d_vset(&llo, 1e30f, 1e30f, 1e30f); s3d_vset(&lhi, -1e30f, -1e30f, -1e30f);
    for (c = 0; c < 8; c++) {   /* the box in the terrain's own space */
        S3dVec3 p;
        s3d_vset(&p, (c & 1) ? hi->x : lo->x, (c & 2) ? hi->y : lo->y, (c & 4) ? hi->z : lo->z);
        s3d_vpoint(&p, &h->inv, &p);
        s3d_vmin(&llo, &p); s3d_vmax(&lhi, &p);
    }
    x0w = -(float)(hard ? h->cols : h->cols - 1) * h->cell * 0.5f; z0w = -(float)(hard ? h->rows : h->rows - 1) * h->cell * 0.5f;
    i0 = s3d_maxi(hard ? -1 : 0, (int)floorf((llo.x - x0w) / h->cell)); i1 = s3d_mini(hard ? h->cols : h->cols - 2, (int)floorf((lhi.x - x0w) / h->cell));
    j0 = s3d_maxi(hard ? -1 : 0, (int)floorf((llo.z - z0w) / h->cell)); j1 = s3d_mini(hard ? h->rows : h->rows - 2, (int)floorf((lhi.z - z0w) / h->cell));
    for (j = j0; j <= j1; j++) for (i = i0; i <= i1; i++) {
        float xa = x0w + (float)i * h->cell, xb = xa + h->cell, za = z0w + (float)j * h->cell, zb = za + h->cell;
        float hc = s3d_heightAt(h, i, j), hr = s3d_heightAt(h, i + 1, j), hd = s3d_heightAt(h, i, j + 1);
        if (!hard) {   /* two triangles a cell (the diagonal from (1, 0) to (0, 1)) */
            S3dVec3 t[3], w[4];
            s3d_vset(&w[0], xa, hc * h->step, za); s3d_vset(&w[1], xb, hr * h->step, za);
            s3d_vset(&w[2], xa, hd * h->step, zb); s3d_vset(&w[3], xb, s3d_heightAt(h, i + 1, j + 1) * h->step, zb);
            for (c = 0; c < 4; c++) s3d_vpoint(&w[c], &h->model, &w[c]);
            t[0] = w[0]; t[1] = w[2]; t[2] = w[1];
            if (visit(ctx, t, 0)) return;
            t[0] = w[1]; t[1] = w[2]; t[2] = w[3];
            if (visit(ctx, t, 0)) return;
            continue;
        }
        if (i >= 0 && j >= 0 && i < h->cols && j < h->rows) {   /* the top of the block */
            float y = hc * h->step;
            s3d_vset(&q[0], xa, y, za); s3d_vset(&q[1], xa, y, zb); s3d_vset(&q[2], xb, y, zb); s3d_vset(&q[3], xb, y, za);
            if (s3d_heightsQuad(h, q, visit, ctx)) return;
        }
        if (j >= 0 && j < h->rows && hc != hr) {   /* the wall at x = xb, to the cell on the right (it faces the lower one) */
            float l = s3d_minf(hc, hr) * h->step, u = s3d_maxf(hc, hr) * h->step;
            if (hc > hr ? s3d_heightsWall(h, xb, zb, xb, za, l, u, visit, ctx) : s3d_heightsWall(h, xb, za, xb, zb, l, u, visit, ctx)) return;
        }
        if (i >= 0 && i < h->cols && hc != hd) {   /* the wall at z = zb, to the cell behind */
            float l = s3d_minf(hc, hd) * h->step, u = s3d_maxf(hc, hd) * h->step;
            if (hc > hd ? s3d_heightsWall(h, xa, zb, xb, zb, l, u, visit, ctx) : s3d_heightsWall(h, xb, zb, xa, zb, l, u, visit, ctx)) return;
        }
    }
}
static void s3d_staticVisit(const S3dBody* b, const S3dVec3* lo, const S3dVec3* hi, S3dTriVisit visit, void* ctx) {
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
    { S3dShape s; s3d_bodyShape(&s, b); *lo = s.lo; *hi = s.hi; }
}

/* the velocity of a body at the point centre + r, the effect of an impulse along n there (its "mass" seen at the point), an impulse */
static void s3d_velAt(S3dVec3* v, const S3dBody* b, const S3dVec3* r) {
    if (b->kind == S3D_BODY_KINEMATIC) { *v = b->moved; return; }
    if (!s3d_dynamic(b)) { s3d_vset(v, 0, 0, 0); return; }
    if (b->rotates) { s3d_vcross(v, &b->w, r); s3d_vadd(v, v, &b->vel); } else *v = b->vel;
}
static void s3d_invIW(S3dVec3* r, const S3dBody* b, const S3dVec3* v) {
    S3dVec3 l;
    s3d_vrotT(&l, b->R, v);
    l.x *= b->invI.x; l.y *= b->invI.y; l.z *= b->invI.z;
    s3d_vrot(r, b->R, &l);
}
static float s3d_kAt(const S3dBody* b, const S3dVec3* r, const S3dVec3* n) {
    S3dVec3 t;
    if (!s3d_dynamic(b)) return 0;
    if (!b->rotates) return b->invMass;
    s3d_vcross(&t, r, n); s3d_invIW(&t, b, &t); s3d_vcross(&t, &t, r);
    return b->invMass + s3d_vdot(n, &t);
}
/* the impulse s * J at the point centre + r */
static void s3d_impulse(S3dBody* b, const S3dVec3* r, const S3dVec3* J, float s) {
    S3dVec3 t;
    if (!s3d_dynamic(b)) return;
    s3d_vmad(&b->vel, &b->vel, J, s * b->invMass);
    if (!b->rotates) return;
    s3d_vcross(&t, r, J); s3d_invIW(&t, b, &t);
    s3d_vmad(&b->w, &b->w, &t, s);
}
/* the velocity of A at a point less the one of B there */
static void s3d_relVel(S3dVec3* v, const S3dBody* A, const S3dVec3* rA, const S3dBody* B, const S3dVec3* rB) {
    S3dVec3 vb;
    s3d_velAt(v, A, rA); s3d_velAt(&vb, B, rB);
    s3d_vsub(v, v, &vb);
}

static void s3d_touch(S3dBody* b, int64_t id) {
    int k;
    for (k = 0; k < b->touchN; k++) if (b->touch[k] == id) return;
    if (b->touchN < S3D_TOUCH_MAX) b->touch[b->touchN++] = id;
}

/* the impulses a pair got in this step, point by point (the sum along n never below 0; the friction at most `friction` times it) */
#define S3D_ACC 16
typedef struct S3dAcc { float jn[S3D_ACC]; S3dVec3 jt[S3D_ACC]; } S3dAcc;

/* The answer to the contacts of a pair: A (dynamic) and B. Bodies that do not turn are pushed out of each other (by their masses; 2 mm of overlap stay, so
 * that the contact lasts); for bodies that turn the push out goes into the velocity at each point (1/5 of the overlap above 5 mm a step - so the friction
 * holds it too). Then at each point the velocities along n meet (with the bounce; a point still apart may come closer, up to touching) and the sideways
 * velocity is slowed by the friction. */
static void s3d_resolve(S3dBody* A, S3dBody* B, const S3dContact* c, int m, S3dAcc* acc, int slot, float dt) {
    float ia = A->invMass, ib = s3d_dynamic(B) ? B->invMass : 0, sum = ia + ib, bounce, fric, depth = 0;
    const S3dVec3* n = &c[0].n;
    int k, turning = A->rotates || (ib > 0 && B->rotates);
    if (sum <= 0) return;
    for (k = 0; k < m; k++) if (c[k].depth > depth) depth = c[k].depth;
    if (n->y >= A->slopeCos) A->onGround = 1;
    if (ib > 0 && -n->y >= B->slopeCos) B->onGround = 1;
    if (!turning) {
        S3dVec3 push;
        depth = s3d_maxf(0, depth - 0.002f);
        /* a character on ground it can stand on: straight up, so that it does not slide down the slope */
        if (A->character && n->y >= A->slopeCos) s3d_vset(&push, 0, depth / n->y, 0); else s3d_vscale(&push, n, depth);
        s3d_vmad(&A->c, &A->c, &push, ia / sum);
        if (ib > 0) s3d_vmad(&B->c, &B->c, n, -depth * ib / sum);
    }
    bounce = s3d_maxf(A->bounce, B->bounce);
    fric = A->character ? 0 : sqrtf(A->friction * B->friction);   /* (a character's sideways velocity is its walk) */
    for (k = 0; k < m; k++) {
        S3dVec3 rA, rB, rel, t;
        float vn, kn, j, vtl, kt;
        int at = slot + k < S3D_ACC && acc ? slot + k : -1;
        s3d_vsub(&rA, &c[k].p, &A->c); s3d_vsub(&rB, &c[k].p, &B->c);
        s3d_relVel(&rel, A, &rA, B, &rB);
        vn = s3d_vdot(&rel, n);
        kn = s3d_kAt(A, &rA, n) + s3d_kAt(B, &rB, n);
        if (kn <= 0) continue;
        if (c[k].depth < 0) j = dt > 0 ? -(vn - c[k].depth / dt) / kn : 0;
        else j = -(vn + (vn < -0.5f ? bounce * vn : 0)) / kn;   /* (slow: it comes to rest) */
        if (turning && c[k].depth > 0.005f && dt > 0) j += 0.2f / dt * (c[k].depth - 0.005f) / kn;
        if (at >= 0) { float sumJ = s3d_maxf(acc->jn[at] + j, 0); j = sumJ - acc->jn[at]; acc->jn[at] = sumJ; }
        else if (j < 0) j = 0;
        if (j != 0) { s3d_impulse(A, &rA, n, j); s3d_impulse(B, &rB, n, -j); }
        if (fric <= 0) continue;
        s3d_relVel(&rel, A, &rA, B, &rB);
        s3d_vmad(&rel, &rel, n, -s3d_vdot(&rel, n));   /* (the sideways part) */
        vtl = s3d_vlen(&rel);
        if (vtl < 1e-7f) continue;
        s3d_vscale(&t, &rel, 1 / vtl);
        kt = s3d_kAt(A, &rA, &t) + s3d_kAt(B, &rB, &t);
        if (kt <= 0) continue;
        if (at >= 0) {   /* the friction of the step so far plus what stops the sliding, at most friction times the push along n */
            S3dVec3 sumT, dj;
            float lim = fric * acc->jn[at], l;
            s3d_vmad(&sumT, &acc->jt[at], &rel, -1 / kt);
            l = s3d_vlen(&sumT);
            if (l > lim && l > 0) s3d_vscale(&sumT, &sumT, lim / l);
            s3d_vsub(&dj, &sumT, &acc->jt[at]); acc->jt[at] = sumT;
            s3d_impulse(A, &rA, &dj, 1); s3d_impulse(B, &rB, &dj, -1);
        } else {
            float jt = s3d_minf(vtl / kt, fric * j);
            s3d_impulse(A, &rA, &t, -jt); s3d_impulse(B, &rB, &t, jt);
        }
    }
}

/* the contacts of A with the triangles of B, collected in groups (one triangle each) */
typedef struct S3dHitList { const S3dShape* shape; S3dContact c[32]; int start[16], count[16], n, cn; } S3dHitList;
static int s3d_collectHit(void* ctx, const S3dVec3* tri, int twoSided) {
    S3dHitList* L = (S3dHitList*)ctx;
    int m;
    if (L->n >= 16 || L->cn + 4 > 32) return 0;
    m = s3d_contactTriangle(L->shape, tri, twoSided, &L->c[L->cn]);
    if (m == 0) return 0;
    L->start[L->n] = L->cn; L->count[L->n] = m; L->n++; L->cn += m;
    return 0;
}

/* The pair A (dynamic) and B: their contacts answered. test: only whether A overlaps B solidly (1). Returns whether they touched. */
static int s3d_pair(S3dBody* A, S3dBody* B, int test, S3dAcc* acc, float dt) {
    S3dShape sa, sb;
    S3dContact c[S3D_MANIFOLD];
    int m, g, k;
    s3d_bodyShape(&sa, A);
    if (B->shape == S3D_SHAPE_MESH) {
        S3dHitList L;
        L.shape = &sa; L.n = 0; L.cn = 0;
        s3d_staticVisit(B, &sa.lo, &sa.hi, s3d_collectHit, &L);
        if (L.n == 0) return 0;
        if (A->trigger || B->trigger) { s3d_touch(A, B->id); s3d_touch(B, A->id); return 0; }
        if (test) { for (k = 0; k < L.cn; k++) if (L.c[k].depth > 0.002f) return 1; return 0; }
        s3d_touch(A, B->id);
        /* the deepest triangle first; each one again after the others moved A */
        for (g = 0; g < L.n; g++) {
            int best = g, q, s, cnt;
            float moved;
            S3dVec3 shift;
            for (q = g + 1; q < L.n; q++) if (L.c[L.start[q]].depth > L.c[L.start[best]].depth) best = q;
            s = L.start[best]; L.start[best] = L.start[g]; L.start[g] = s;
            cnt = L.count[best]; L.count[best] = L.count[g]; L.count[g] = cnt;
            /* how deep it still is: along n, what A moved since */
            s3d_vsub(&shift, &A->c, &sa.c);
            moved = s3d_vdot(&shift, &L.c[s].n);
            for (k = 0; k < cnt; k++) { c[k] = L.c[s + k]; c[k].depth -= moved; s3d_vadd(&c[k].p, &c[k].p, &shift); }
            s3d_resolve(A, B, c, cnt, acc, g * 4, dt);
        }
        return 1;
    }
    s3d_bodyShape(&sb, B);
    if (!s3d_boxesMeet(&sa.lo, &sa.hi, &sb.lo, &sb.hi) || (m = s3d_collide(&sa, &sb, c)) == 0) return 0;
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
static int s3d_raycast(S3dBody** bodies, int n, const S3dVec3* o, const S3dVec3* d, float maxDist, int skip, S3dRayHit* hit);

/* a joint: positions and velocities along it (rope and rod), or its force (a spring, once a step) */
static void s3d_joint(const S3dJoint* j, float dt, int spring) {
    S3dBody* A = j->a;
    S3dBody* B = j->b;
    S3dVec3 ra, rb, pa, pb, n, vrel, vb;
    float dist, C, vn, ia, ib, sum;
    if ((j->kind == S3D_JOINT_SPRING) != spring) return;
    s3d_vrot(&ra, A->R, &j->la); s3d_vadd(&pa, &A->c, &ra);
    if (B) { s3d_vrot(&rb, B->R, &j->lb); s3d_vadd(&pb, &B->c, &rb); } else { s3d_vset(&rb, 0, 0, 0); pb = j->lb; }
    s3d_vsub(&n, &pb, &pa);
    dist = s3d_vnorm(&n, &n);
    if (dist < 1e-6f) return;
    C = dist - j->length;
    s3d_velAt(&vrel, A, &ra);
    if (B) { s3d_velAt(&vb, B, &rb); s3d_vsub(&vrel, &vb, &vrel); } else s3d_vscale(&vrel, &vrel, -1);
    vn = s3d_vdot(&vrel, &n);   /* > 0: they move apart */
    if (spring) {
        float f = (j->stiffness * C + j->damping * vn) * dt;
        s3d_impulse(A, &ra, &n, f);
        if (B) s3d_impulse(B, &rb, &n, -f);
        if (fabsf(C) > 0.001f) { s3d_wake(A); if (B) s3d_wake(B); }
        return;
    }
    if (j->kind == S3D_JOINT_ROPE && C <= 0) return;   /* (a rope that hangs loose) */
    ia = s3d_dynamic(A) ? A->invMass : 0; ib = B && s3d_dynamic(B) ? B->invMass : 0; sum = ia + ib;
    if (sum <= 0) return;
    if (fabsf(C) > 0.001f) { s3d_wake(A); if (B) s3d_wake(B); }
    s3d_vmad(&A->c, &A->c, &n, C * ia / sum);
    if (B && ib > 0) s3d_vmad(&B->c, &B->c, &n, -C * ib / sum);
    if (j->kind == S3D_JOINT_ROPE && vn <= 0) return;
    {
        float k = s3d_kAt(A, &ra, &n) + (B ? s3d_kAt(B, &rb, &n) : 0);
        if (k <= 0) return;
        s3d_impulse(A, &ra, &n, vn / k);
        if (B) s3d_impulse(B, &rb, &n, -vn / k);
    }
}

/* the pairs: the boxes sorted along x; those that overlap and where one is an awake dynamic body (or a sleeping one meets a kinematic one), A the
 * dynamic one (awake first). Returns how many (pairs: 2 indices each; NULL: none or no memory). */
static int s3d_pairs(S3dBody** bodies, int n, float dt, int** out) {
    int i, k, pn = 0, *order = (int*)S3D_MALLOC(sizeof(int) * (s3d_size)(n + 1)), *pairs = NULL;
    S3dVec3* lo = (S3dVec3*)S3D_MALLOC(sizeof(S3dVec3) * (s3d_size)(n + 1) * 2);
    S3dVec3* hi = lo ? lo + n + 1 : NULL;
    s3d_size cap = 0;
    *out = NULL;
    if (order && lo) {
        for (i = 0; i < n; i++) {
            const S3dBody* b = bodies[i];
            S3dVec3 grow;
            int at = i;
            s3d_bodyBox(b, &lo[i], &hi[i]);
            /* (grown by 5 cm and by what it moves in this step) */
            if (b->kind == S3D_BODY_STATIC) s3d_vset(&grow, 0.05f, 0.05f, 0.05f);
            else s3d_vset(&grow, 0.05f + fabsf(b->vel.x) * dt, 0.05f + fabsf(b->vel.y) * dt, 0.05f + fabsf(b->vel.z) * dt);
            s3d_vsub(&lo[i], &lo[i], &grow); s3d_vadd(&hi[i], &hi[i], &grow);
            while (at > 0 && lo[order[at - 1]].x > lo[i].x) { order[at] = order[at - 1]; at--; }
            order[at] = i;
        }
        for (i = 0; i < n; i++) {
            int a = order[i];
            for (k = i + 1; k < n; k++) {
                int b = order[k];
                S3dBody* ba = bodies[a];
                S3dBody* bb = bodies[b];
                if (lo[b].x > hi[a].x) break;
                if (!s3d_boxesMeet(&lo[a], &hi[a], &lo[b], &hi[b])) continue;
                if (!((s3d_dynamic(ba) && !ba->asleep) || (s3d_dynamic(bb) && !bb->asleep)
                    || (s3d_dynamic(ba) && bb->kind == S3D_BODY_KINEMATIC) || (s3d_dynamic(bb) && ba->kind == S3D_BODY_KINEMATIC))) continue;
                if (!S3D_GROW(pairs, cap, (s3d_size)(pn + 1) * 2)) break;
                if (s3d_dynamic(ba) && (!ba->asleep || !s3d_dynamic(bb) || bb->asleep)) { pairs[pn * 2] = a; pairs[pn * 2 + 1] = b; }
                else { pairs[pn * 2] = b; pairs[pn * 2 + 1] = a; }
                pn++;
            }
        }
    }
    S3D_FREE(order); S3D_FREE(lo);
    *out = pairs;
    return pn;
}

/* a character walking against a step: blocked sideways where it is a step higher free - it goes up, and then down onto the step */
static void s3d_stepUp(S3dBody** bodies, int n, int i) {
    S3dBody* b = bodies[i];
    S3dVec3 at = b->c, from, down;
    S3dRayHit hit;
    S3dShape s;
    b->c.y += b->stepHeight;
    if (!s3d_overlapsAny(bodies, n, i)) {
        s3d_bodyShape(&s, b);
        s3d_vset(&from, s.a.x, s.a.y - s.r + 0.01f, s.a.z); s3d_vset(&down, 0, -1, 0);
        if (s3d_raycast(bodies, n, &from, &down, b->stepHeight + 0.02f, i, &hit) >= 0 && hit.normal.y >= b->slopeCos) {
            b->c.y -= s3d_maxf(0, hit.dist - 0.008f);   /* (2 mm into the step: it stands on it) */
            if (!s3d_overlapsAny(bodies, n, i)) { b->onGround = 1; if (b->vel.y < 0) b->vel.y = 0; s3d_touch(b, bodies[hit.index]->id); return; }
        }
    }
    b->c = at;   /* (no room, or nothing to stand on below: no step) */
}

/* One step of dt seconds for the bodies (gravity: the acceleration of the world, e.g. 0, -9.81, 0) and their joints. */
static void s3d_physicsStep(S3dBody** bodies, int n, const S3dJoint* joints, int jn, const S3dVec3* gravity, float dt) {
    int i, k, it, pn, *pairs;
    S3dAcc* accs;
    /* the velocities first: gravity, damping, the walk - then the contacts answer them, and only then the bodies move (semi-implicit) */
    for (i = 0; i < n; i++) {
        S3dBody* b = bodies[i];
        b->touchN = 0;
        if (!s3d_dynamic(b) || b->asleep) continue;
        b->wasOnGround = b->onGround;
        b->onGround = 0;
        s3d_vmad(&b->vel, &b->vel, gravity, b->gravity * dt);
        if (b->damping > 0) s3d_vscale(&b->vel, &b->vel, s3d_maxf(0, 1 - b->damping * dt));
        if (b->rotates) s3d_vscale(&b->w, &b->w, s3d_maxf(0, 1 - b->angularDamping * dt));
        if (b->character) {
            b->vel.x = b->walkX; b->vel.z = b->walkZ;
            if (b->jump > 0) { if (b->wasOnGround) b->vel.y = b->jump; b->jump = 0; }
        }
    }
    for (k = 0; k < jn; k++) s3d_joint(&joints[k], dt, 1);
    pn = s3d_pairs(bodies, n, dt, &pairs);
    accs = pn > 0 ? (S3dAcc*)s3d_calloc((s3d_size)pn, sizeof(S3dAcc)) : NULL;
    for (it = 0; it < 6; it++) {
        if (it == 0)
            for (i = 0; i < n; i++)
                if (bodies[i]->character && bodies[i]->wasOnGround && (bodies[i]->walkX != 0 || bodies[i]->walkZ != 0)) s3d_stepUp(bodies, n, i);
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
    S3D_FREE(pairs); S3D_FREE(accs);
    /* now they move and turn; resting for half a second: asleep (not characters: they wait for their walk) */
    for (i = 0; i < n; i++) {
        S3dBody* b = bodies[i];
        if (b->kind == S3D_BODY_KINEMATIC) { s3d_vmad(&b->c, &b->c, &b->vel, dt); continue; }
        if (!s3d_dynamic(b) || b->asleep) continue;
        s3d_vmad(&b->c, &b->c, &b->vel, dt);
        if (b->rotates) s3d_turnBy(b, &b->w, dt);
        if (b->character) continue;
        if (s3d_vdot(&b->vel, &b->vel) < 0.0025f && s3d_vdot(&b->w, &b->w) < 0.0025f) b->sleepTime += dt; else b->sleepTime = 0;
        if (b->sleepTime > 0.5f) { b->asleep = 1; s3d_vset(&b->vel, 0, 0, 0); s3d_vset(&b->w, 0, 0, 0); }
    }
}

/* ---- rays ---- */
typedef struct S3dRay { S3dVec3 o, d, n; float best; } S3dRay;
static int s3d_rayTriangle(void* ctx, const S3dVec3* tri, int twoSided) {
    S3dRay* r = (S3dRay*)ctx;
    S3dVec3 e1, e2, p, q, tv;
    float det, inv, u, v, t;
    (void)twoSided;
    s3d_vsub(&e1, &tri[1], &tri[0]); s3d_vsub(&e2, &tri[2], &tri[0]);
    s3d_vcross(&p, &r->d, &e2);
    det = s3d_vdot(&e1, &p);
    if (fabsf(det) < 1e-12f) return 0;
    inv = 1 / det; s3d_vsub(&tv, &r->o, &tri[0]);
    u = s3d_vdot(&tv, &p) * inv;
    if (u < 0 || u > 1) return 0;
    s3d_vcross(&q, &tv, &e1);
    v = s3d_vdot(&r->d, &q) * inv;
    if (v < 0 || u + v > 1) return 0;
    t = s3d_vdot(&e2, &q) * inv;
    if (t >= 0 && t < r->best) {
        r->best = t;
        s3d_vcross(&r->n, &e1, &e2); s3d_vnorm(&r->n, &r->n);
        if (s3d_vdot(&r->n, &r->d) > 0) s3d_vscale(&r->n, &r->n, -1);
    }
    return 0;
}
/* a ray against a box in its own space (slabs): the distance or -1, the normal of the face (in the world) */
static float s3d_rayBox(const S3dShape* s, const S3dVec3* o, const S3dVec3* d, S3dVec3* n) {
    S3dVec3 lo;
    float tmin = -1e30f, tmax = 1e30f, oo[3], dd[3], h[3];
    int k, axis = 1;
    s3d_toBox(&lo, s, o);
    oo[0] = lo.x; oo[1] = lo.y; oo[2] = lo.z; h[0] = s->h.x; h[1] = s->h.y; h[2] = s->h.z;
    for (k = 0; k < 3; k++) dd[k] = s3d_vdot(d, &s->ax[k]);
    for (k = 0; k < 3; k++) {
        float t1, t2;
        if (fabsf(dd[k]) < 1e-12f) { if (oo[k] < -h[k] || oo[k] > h[k]) return -1; continue; }
        t1 = (-h[k] - oo[k]) / dd[k]; t2 = (h[k] - oo[k]) / dd[k];
        if (t1 > t2) { float x = t1; t1 = t2; t2 = x; }
        if (t1 > tmin) { tmin = t1; axis = k; }
        if (t2 < tmax) tmax = t2;
        if (tmin > tmax) return -1;
    }
    if (tmax < 0) return -1;
    if (tmin < 0) { s3d_vscale(n, d, -1); return 0; }   /* (inside) */
    s3d_vscale(n, &s->ax[axis], dd[axis] > 0 ? -1.0f : 1.0f);
    return tmin;
}
/* a ray against a sphere at c: the distance or -1, and the normal there */
static float s3d_raySphere(const S3dVec3* o, const S3dVec3* d, const S3dVec3* c, float r, S3dVec3* n) {
    S3dVec3 m;
    float b, cc, disc, t;
    s3d_vsub(&m, o, c);
    b = s3d_vdot(&m, d); cc = s3d_vdot(&m, &m) - r * r;
    if (cc > 0 && b > 0) return -1;
    disc = b * b - cc;
    if (disc < 0) return -1;
    t = -b - sqrtf(disc);
    if (t < 0) t = 0;
    s3d_vmad(n, &m, d, t); s3d_vnorm(n, n);
    return t;
}
/* a ray against an upright capsule (segment a..b, a below): the side (a cylinder), then the two spheres */
static float s3d_rayCapsule(const S3dVec3* o, const S3dVec3* d, const S3dShape* s, S3dVec3* n) {
    float best = -1, t, A = d->x * d->x + d->z * d->z, ox = o->x - s->a.x, oz = o->z - s->a.z, B = ox * d->x + oz * d->z, C = ox * ox + oz * oz - s->r * s->r;
    S3dVec3 nn;
    int k;
    if (A > 1e-12f && B * B - A * C >= 0) {
        t = (-B - sqrtf(B * B - A * C)) / A;
        if (t >= 0 && o->y + d->y * t >= s->a.y && o->y + d->y * t <= s->b.y) { best = t; s3d_vset(n, ox + d->x * t, 0, oz + d->z * t); s3d_vnorm(n, n); }
    }
    for (k = 0; k < 2; k++) {
        t = s3d_raySphere(o, d, k ? &s->b : &s->a, s->r, &nn);
        if (t >= 0 && (best < 0 || t < best)) { best = t; *n = nn; }
    }
    return best;
}
/* The nearest body a ray meets (d: a unit vector; up to maxDist; not body `skip`, not triggers): its index or -1. */
static int s3d_raycast(S3dBody** bodies, int n, const S3dVec3* o, const S3dVec3* d, float maxDist, int skip, S3dRayHit* hit) {
    int k, found = -1;
    float best = maxDist;
    S3dVec3 bestN, end, rlo, rhi;
    s3d_vset(&bestN, 0, 1, 0);
    s3d_vmad(&end, o, d, maxDist);
    rlo = rhi = *o; s3d_vmin(&rlo, &end); s3d_vmax(&rhi, &end);
    for (k = 0; k < n; k++) {
        S3dBody* b = bodies[k];
        S3dVec3 nn;
        float t = -1;
        if (k == skip || b->trigger) continue;
        s3d_vset(&nn, 0, 1, 0);
        if (b->shape == S3D_SHAPE_MESH) {
            S3dRay r;
            r.o = *o; r.d = *d; r.best = best; r.n = nn;
            s3d_staticVisit(b, &rlo, &rhi, s3d_rayTriangle, &r);
            if (r.best < best) { t = r.best; nn = r.n; }
        } else {
            S3dShape s;
            s3d_bodyShape(&s, b);
            if (!s3d_boxesMeet(&rlo, &rhi, &s.lo, &s.hi)) continue;
            if (s.box) t = s3d_rayBox(&s, o, d, &nn);
            else if (b->shape == S3D_SHAPE_SPHERE) t = s3d_raySphere(o, d, &s.a, s.r, &nn);
            else t = s3d_rayCapsule(o, d, &s, &nn);
        }
        if (t >= 0 && t < best) { best = t; bestN = nn; found = k; }
    }
    if (found >= 0) { hit->dist = best; s3d_vmad(&hit->point, o, d, best); hit->normal = bestN; hit->index = found; }
    return found;
}

/* The inverse of a model matrix (rotation, scale, move; no projection). */
static S3dMat4 s3d_affineInverse(const S3dMat4* m) {
    S3dMat4 r = s3d_identity();
    float a = S3D_AT(*m, 0, 0), b = S3D_AT(*m, 0, 1), c = S3D_AT(*m, 0, 2), d = S3D_AT(*m, 1, 0), e = S3D_AT(*m, 1, 1), f = S3D_AT(*m, 1, 2), g = S3D_AT(*m, 2, 0), h = S3D_AT(*m, 2, 1), i = S3D_AT(*m, 2, 2);
    float A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g, det = a * A + b * B + c * C, inv;
    int k;
    if (fabsf(det) < 1e-20f) return r;
    inv = 1 / det;
    S3D_AT(r, 0, 0) = A * inv; S3D_AT(r, 0, 1) = -(b * i - c * h) * inv; S3D_AT(r, 0, 2) = (b * f - c * e) * inv;
    S3D_AT(r, 1, 0) = B * inv; S3D_AT(r, 1, 1) = (a * i - c * g) * inv; S3D_AT(r, 1, 2) = -(a * f - c * d) * inv;
    S3D_AT(r, 2, 0) = C * inv; S3D_AT(r, 2, 1) = -(a * h - b * g) * inv; S3D_AT(r, 2, 2) = (a * e - b * d) * inv;
    for (k = 0; k < 3; k++) S3D_AT(r, k, 3) = -(S3D_AT(r, k, 0) * S3D_AT(*m, 0, 3) + S3D_AT(r, k, 1) * S3D_AT(*m, 1, 3) + S3D_AT(r, k, 2) * S3D_AT(*m, 2, 3));
    return r;
}

#endif /* FIRE_S3D_PHYSICS_H */
