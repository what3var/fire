/* fire native graphics: the image slicer - turns a mask into tool paths of a fixed line width (Euclidean distance transform, marching squares, simplification).
 * A port of src/fire.Terminal/Slicing/ImageSlicer.cs; the same paths come out (the order of the contours too: a contour starts at the first edge that was linked). */
#ifndef FIRE_GFX_SLICER_H
#define FIRE_GFX_SLICER_H

#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "fire_gfx.h"

typedef struct FireGfxPointD { double x, y; } FireGfxPointD;
enum { FIRE_GFX_PATH_FILL = 0, FIRE_GFX_PATH_OUTLINE = 1 };
enum { FIRE_GFX_STRATEGY_CONTOUR = 0, FIRE_GFX_STRATEGY_ZIGZAG = 1, FIRE_GFX_STRATEGY_OUTLINE_ONLY = 2 };
typedef struct FireGfxToolPath { FireGfxPointD* points; fire_gfx_size count; fire_gfx_int closed; fire_gfx_int kind; } FireGfxToolPath;
typedef struct FireGfxToolPaths { FireGfxToolPath* items; fire_gfx_size count, cap; fire_gfx_int failed; } FireGfxToolPaths;

static void fire_gfx_pathsFree(FireGfxToolPaths* p) {
    fire_gfx_size i;
    for (i = 0; i < p->count; i++) fire_gfx_free(p->items[i].points);
    fire_gfx_free(p->items);
    p->items = NULL; p->count = 0; p->cap = 0;
}
/* Appends a path (its points are taken over). */
static void fire_gfx_pathsAdd(FireGfxToolPaths* p, FireGfxPointD* points, fire_gfx_size count, fire_gfx_int closed, fire_gfx_int kind) {
    if (!fire_gfx_grow((void**)&p->items, &p->cap, p->count, sizeof(FireGfxToolPath))) { fire_gfx_free(points); p->failed = 1; return; }
    p->items[p->count].points = points; p->items[p->count].count = count; p->items[p->count].closed = closed; p->items[p->count].kind = kind;
    p->count++;
}

typedef struct FireGfxSlicer {
    double lineWidth, pixelSize, overlap, simplifyTolerance;   /* simplifyTolerance: NaN = a quarter of a pixel */
    fire_gfx_int strategy;
    fire_gfx_int flipY;
} FireGfxSlicer;

static void fire_gfx_slicerInit(FireGfxSlicer* s) { s->lineWidth = 0; s->pixelSize = 0; s->overlap = 0.5; s->simplifyTolerance = 0; s->strategy = FIRE_GFX_STRATEGY_CONTOUR; s->flipY = 1; }
static double fire_gfx_slicerStepOver(const FireGfxSlicer* s) { return s->lineWidth * (1.0 - s->overlap); }

/* A grid indexed [x][y], x major. */
typedef struct FireGfxGrid { fire_gfx_int W, H; double* v; } FireGfxGrid;
#define FIRE_GFX_AT(g, x, y) ((g)->v[(fire_gfx_size)(x) * (g)->H + (y)])

static void fire_gfx_edt1d(const double* f, fire_gfx_int n, double* d, fire_gfx_int* v, double* z) {
    fire_gfx_int k = 0, q;
    v[0] = 0;
    z[0] = -INFINITY;
    z[1] = INFINITY;
    for (q = 1; q < n; q++) {
        double s = ((f[(fire_gfx_size)q] + (double)q * q) - (f[(fire_gfx_size)v[(fire_gfx_size)k]] + (double)v[(fire_gfx_size)k] * v[(fire_gfx_size)k])) / (2.0 * q - 2.0 * v[(fire_gfx_size)k]);
        while (s <= z[(fire_gfx_size)k]) {
            k--;
            s = ((f[(fire_gfx_size)q] + (double)q * q) - (f[(fire_gfx_size)v[(fire_gfx_size)k]] + (double)v[(fire_gfx_size)k] * v[(fire_gfx_size)k])) / (2.0 * q - 2.0 * v[(fire_gfx_size)k]);
        }
        k++;
        v[(fire_gfx_size)k] = q;
        z[(fire_gfx_size)k] = s;
        z[(fire_gfx_size)k + 1] = INFINITY;
    }
    k = 0;
    for (q = 0; q < n; q++) {
        double dq;
        while (z[(fire_gfx_size)k + 1] < q) k++;
        dq = q - v[(fire_gfx_size)k];
        d[(fire_gfx_size)q] = dq * dq + f[(fire_gfx_size)v[(fire_gfx_size)k]];
    }
}

/* 0 without memory. */
static fire_gfx_int fire_gfx_distanceField(const char* mask, fire_gfx_int w, fire_gfx_int h, FireGfxGrid* d) {
    double inf;
    fire_gfx_int n, x, y;
    double* f;
    double* outp;
    double* z;
    fire_gfx_int* v;
    d->W = w + 2; d->H = h + 2;
    d->v = (double*)fire_gfx_calloc((fire_gfx_size)d->W * d->H, sizeof(double));
    if (d->v == NULL) return 0;
    inf = (double)(d->W + d->H) * (d->W + d->H);
    for (y = 0; y < d->H; y++)
        for (x = 0; x < d->W; x++) {
            fire_gfx_int inside = x >= 1 && x <= w && y >= 1 && y <= h && mask[(fire_gfx_size)(x - 1) * h + (y - 1)];
            FIRE_GFX_AT(d, x, y) = inside ? inf : 0.0;
        }
    n = fire_gfx_imax(d->W, d->H);
    f = (double*)fire_gfx_calloc((fire_gfx_size)n, sizeof(double));
    outp = (double*)fire_gfx_calloc((fire_gfx_size)n, sizeof(double));
    z = (double*)fire_gfx_calloc((fire_gfx_size)n + 1, sizeof(double));
    v = (fire_gfx_int*)fire_gfx_calloc((fire_gfx_size)n, sizeof(fire_gfx_int));
    if (f == NULL || outp == NULL || z == NULL || v == NULL) { fire_gfx_free(f); fire_gfx_free(outp); fire_gfx_free(z); fire_gfx_free(v); fire_gfx_free(d->v); d->v = NULL; return 0; }
    for (x = 0; x < d->W; x++) {
        for (y = 0; y < d->H; y++) f[(fire_gfx_size)y] = FIRE_GFX_AT(d, x, y);
        fire_gfx_edt1d(f, d->H, outp, v, z);
        for (y = 0; y < d->H; y++) FIRE_GFX_AT(d, x, y) = outp[(fire_gfx_size)y];
    }
    for (y = 0; y < d->H; y++) {
        for (x = 0; x < d->W; x++) f[(fire_gfx_size)x] = FIRE_GFX_AT(d, x, y);
        fire_gfx_edt1d(f, d->W, outp, v, z);
        for (x = 0; x < d->W; x++) FIRE_GFX_AT(d, x, y) = sqrt(outp[(fire_gfx_size)x]);
    }
    fire_gfx_free(f); fire_gfx_free(outp); fire_gfx_free(z); fire_gfx_free(v);
    return 1;
}

/* ---- marching squares: the edges of the cells that the level crosses, linked to neighbours; every edge has at most two (one per cell it belongs to) ---- */
typedef struct FireGfxEdge { int64_t key; int64_t nb[2]; fire_gfx_int count; fire_gfx_int visited; } FireGfxEdge;
typedef struct FireGfxEdges {
    FireGfxEdge* items;   /* in the order they were added (the iteration order of the VM's dictionary) */
    fire_gfx_size count, cap;
    int32_t* slots;       /* open addressing: index + 1 into `items`, 0 free */
    fire_gfx_size mask;
    fire_gfx_int failed;
} FireGfxEdges;

static fire_gfx_size fire_gfx_edgeHash(int64_t key, fire_gfx_size mask) { return (fire_gfx_size)(((uint64_t)key * 0x9E3779B97F4A7C15ull) >> 32) & mask; }
static FireGfxEdge* fire_gfx_edgeFind(const FireGfxEdges* e, int64_t key) {
    fire_gfx_size at;
    if (e->slots == NULL) return NULL;
    at = fire_gfx_edgeHash(key, e->mask);
    while (e->slots[at] != 0) {
        FireGfxEdge* x = &e->items[e->slots[at] - 1];
        if (x->key == key) return x;
        at = (at + 1) & e->mask;
    }
    return NULL;
}
static fire_gfx_int fire_gfx_edgeRehash(FireGfxEdges* e, fire_gfx_size slots) {
    int32_t* table = (int32_t*)fire_gfx_calloc(slots, sizeof(int32_t));
    fire_gfx_size i;
    if (table == NULL) return 0;
    fire_gfx_free(e->slots);
    e->slots = table;
    e->mask = slots - 1;
    for (i = 0; i < e->count; i++) {
        fire_gfx_size at = fire_gfx_edgeHash(e->items[i].key, e->mask);
        while (e->slots[at] != 0) at = (at + 1) & e->mask;
        e->slots[at] = (int32_t)(i + 1);
    }
    return 1;
}
/* adj[from].push_back(to) */
static void fire_gfx_edgeHalf(FireGfxEdges* e, int64_t from, int64_t to) {
    FireGfxEdge* x = fire_gfx_edgeFind(e, from);
    if (x == NULL) {
        fire_gfx_size at;
        if (e->failed) return;
        if (!fire_gfx_grow((void**)&e->items, &e->cap, e->count, sizeof(FireGfxEdge)) || e->count >= 0x7FFFFFFE) { e->failed = 1; return; }
        if (e->slots == NULL || (e->count + 1) * 2 > e->mask + 1) {
            if (!fire_gfx_edgeRehash(e, e->slots == NULL ? 1024 : (e->mask + 1) * 2)) { e->failed = 1; return; }
        }
        x = &e->items[e->count];
        x->key = from; x->count = 0; x->visited = 0;
        at = fire_gfx_edgeHash(from, e->mask);
        while (e->slots[at] != 0) at = (at + 1) & e->mask;
        e->slots[at] = (int32_t)(e->count + 1);
        e->count++;
    }
    if (x->count < 2) x->nb[x->count++] = to;
}
static void fire_gfx_edgeLink(FireGfxEdges* e, int64_t e1, int64_t e2) { fire_gfx_edgeHalf(e, e1, e2); fire_gfx_edgeHalf(e, e2, e1); }

typedef struct FireGfxContour { FireGfxPointD* points; fire_gfx_size count, cap; } FireGfxContour;
typedef struct FireGfxContours { FireGfxContour* items; fire_gfx_size count, cap; } FireGfxContours;
static void fire_gfx_contoursFree(FireGfxContours* c) { fire_gfx_size i; for (i = 0; i < c->count; i++) fire_gfx_free(c->items[i].points); fire_gfx_free(c->items); c->items = NULL; c->count = c->cap = 0; }

static FireGfxPointD fire_gfx_edgePoint(const FireGfxGrid* d, int64_t key, double level) {
    int64_t cell = key >> 1;
    fire_gfx_int x = (fire_gfx_int)(cell % d->W), y = (fire_gfx_int)(cell / d->W);
    FireGfxPointD p;
    if ((key & 1) == 0) {
        double v0 = FIRE_GFX_AT(d, x, y), v1 = FIRE_GFX_AT(d, x + 1, y);
        p.x = x + (level - v0) / (v1 - v0); p.y = (double)y;
        return p;
    } else {
        double v0 = FIRE_GFX_AT(d, x, y), v1 = FIRE_GFX_AT(d, x, y + 1);
        p.x = (double)x; p.y = y + (level - v0) / (v1 - v0);
        return p;
    }
}

static fire_gfx_int fire_gfx_traceContours(const FireGfxGrid* d, double level, FireGfxContours* contours) {
    fire_gfx_int W = d->W, H = d->H, x, y;
    FireGfxEdges adj;
    fire_gfx_size s;
    fire_gfx_set(&adj, 0, sizeof adj);
    for (y = 0; y < H - 1; y++)
        for (x = 0; x < W - 1; x++) {
            double a = FIRE_GFX_AT(d, x, y), b = FIRE_GFX_AT(d, x + 1, y), c = FIRE_GFX_AT(d, x + 1, y + 1), e = FIRE_GFX_AT(d, x, y + 1);
            fire_gfx_int idx = (a >= level ? 8 : 0) | (b >= level ? 4 : 0) | (c >= level ? 2 : 0) | (e >= level ? 1 : 0);
            int64_t T, R, B, L;
            fire_gfx_int centerIn;
            if (idx == 0 || idx == 15) continue;
            T = ((int64_t)y * W + x) << 1; R = ((((int64_t)y * W + (x + 1)) << 1) | 1); B = ((int64_t)(y + 1) * W + x) << 1; L = ((((int64_t)y * W + x) << 1) | 1);
            centerIn = (a + b + c + e) / 4.0 >= level;
            switch (idx) {
                case 1: case 14: fire_gfx_edgeLink(&adj, L, B); break;
                case 2: case 13: fire_gfx_edgeLink(&adj, B, R); break;
                case 3: case 12: fire_gfx_edgeLink(&adj, L, R); break;
                case 4: case 11: fire_gfx_edgeLink(&adj, T, R); break;
                case 6: case 9: fire_gfx_edgeLink(&adj, T, B); break;
                case 7: case 8: fire_gfx_edgeLink(&adj, L, T); break;
                case 5: if (centerIn) { fire_gfx_edgeLink(&adj, L, T); fire_gfx_edgeLink(&adj, B, R); } else { fire_gfx_edgeLink(&adj, T, R); fire_gfx_edgeLink(&adj, L, B); } break;
                case 10: if (centerIn) { fire_gfx_edgeLink(&adj, L, B); fire_gfx_edgeLink(&adj, T, R); } else { fire_gfx_edgeLink(&adj, L, T); fire_gfx_edgeLink(&adj, B, R); } break;
                default: break;
            }
        }
    if (adj.failed) { fire_gfx_free(adj.items); fire_gfx_free(adj.slots); return 0; }
    for (s = 0; s < adj.count; s++) {
        int64_t start = adj.items[s].key, prev = -1, cur = start;
        FireGfxContour poly = {NULL, 0, 0};
        if (adj.items[s].visited) continue;
        for (;;) {
            FireGfxEdge* node = fire_gfx_edgeFind(&adj, cur);
            FireGfxEdge* nextNode;
            int64_t next;
            node->visited = 1;
            if (!fire_gfx_grow((void**)&poly.points, &poly.cap, poly.count, sizeof(FireGfxPointD))) { fire_gfx_free(poly.points); fire_gfx_free(adj.items); fire_gfx_free(adj.slots); return 0; }
            poly.points[poly.count++] = fire_gfx_edgePoint(d, cur, level);
            next = node->nb[0] != prev ? node->nb[0] : node->count > 1 ? node->nb[1] : -1;
            if (next == -1 || next == start) break;
            nextNode = fire_gfx_edgeFind(&adj, next);
            if (nextNode == NULL || nextNode->visited) break;
            prev = cur;
            cur = next;
        }
        if (poly.count >= 2) {
            if (!fire_gfx_grow((void**)&contours->items, &contours->cap, contours->count, sizeof(FireGfxContour))) { fire_gfx_free(poly.points); fire_gfx_free(adj.items); fire_gfx_free(adj.slots); return 0; }
            contours->items[contours->count++] = poly;
        } else fire_gfx_free(poly.points);
    }
    fire_gfx_free(adj.items);
    fire_gfx_free(adj.slots);
    return 1;
}

static FireGfxPointD fire_gfx_toMm(const FireGfxSlicer* sl, double gx, double gy, fire_gfx_int h) {
    FireGfxPointD p;
    p.x = (gx - 0.5) * sl->pixelSize;
    p.y = sl->flipY ? (h - (gy - 0.5)) * sl->pixelSize : (gy - 0.5) * sl->pixelSize;
    return p;
}

static double fire_gfx_segmentDistance(FireGfxPointD p, FireGfxPointD a, FireGfxPointD b) {
    double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
    double t = len2 > 0 ? ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2 : 0;
    double px, py;
    t = fire_gfx_dmax(0.0, fire_gfx_dmin(1.0, t));
    px = a.x + t * dx - p.x; py = a.y + t * dy - p.y;
    return sqrt(px * px + py * py);
}

/* Douglas-Peucker (a closed contour is simplified with its first point repeated). Takes `pts` over and returns the result (*count updated); NULL without memory. */
static FireGfxPointD* fire_gfx_simplify(FireGfxPointD* pts, fire_gfx_size* count, double tol) {
    fire_gfx_size n = *count, i, kept = 0, top = 0;
    FireGfxPointD* work;
    char* keep;
    fire_gfx_int* stack;   /* pairs s, e */
    if (tol <= 0 || n < 4) return pts;
    work = (FireGfxPointD*)fire_gfx_malloc(sizeof(FireGfxPointD) * (n + 1));
    keep = (char*)fire_gfx_calloc(n + 1, 1);
    stack = (fire_gfx_int*)fire_gfx_malloc(sizeof(fire_gfx_int) * 2 * (n + 1));
    if (work == NULL || keep == NULL || stack == NULL) { fire_gfx_free(work); fire_gfx_free(keep); fire_gfx_free(stack); fire_gfx_free(pts); return NULL; }
    fire_gfx_copy(work, pts, sizeof(FireGfxPointD) * n);
    work[n] = pts[0];
    keep[0] = keep[n] = 1;
    stack[0] = 0; stack[1] = (fire_gfx_int)n; top = 1;
    while (top > 0) {
        fire_gfx_int s, e, index = -1, k;
        double maxDist = 0;
        top--;
        s = stack[2 * top]; e = stack[2 * top + 1];
        for (k = s + 1; k < e; k++) {
            double dist = fire_gfx_segmentDistance(work[(fire_gfx_size)k], work[(fire_gfx_size)s], work[(fire_gfx_size)e]);
            if (dist > maxDist) { maxDist = dist; index = k; }
        }
        if (index >= 0 && maxDist > tol) {
            keep[(fire_gfx_size)index] = 1;
            stack[2 * top] = s; stack[2 * top + 1] = index; top++;
            stack[2 * top] = index; stack[2 * top + 1] = e; top++;
        }
    }
    /* (the stack never holds more pairs than points: every pair that is pushed splits a range at a point that is kept) */
    for (i = 0; i < n + 1; i++) if (keep[i]) work[kept++] = work[i];
    kept--;   /* (the repeated first point) */
    fire_gfx_free(keep);
    fire_gfx_free(stack);
    if (kept >= 2) { fire_gfx_free(pts); *count = kept; return work; }
    fire_gfx_free(work);
    return pts;
}

static void fire_gfx_toPaths(const FireGfxSlicer* sl, const FireGfxContours* contours, fire_gfx_int h, fire_gfx_int kind, FireGfxToolPaths* result) {
    double tol = isnan(sl->simplifyTolerance) ? sl->pixelSize * 0.25 : sl->simplifyTolerance;
    fire_gfx_size c, i;
    for (c = 0; c < contours->count; c++) {
        const FireGfxContour* contour = &contours->items[c];
        fire_gfx_size n = contour->count;
        FireGfxPointD* pts = (FireGfxPointD*)fire_gfx_malloc(sizeof(FireGfxPointD) * (n ? n : 1));
        if (pts == NULL) { result->failed = 1; return; }
        for (i = 0; i < n; i++) pts[i] = fire_gfx_toMm(sl, contour->points[i].x, contour->points[i].y, h);
        pts = fire_gfx_simplify(pts, &n, tol);
        if (pts == NULL) { result->failed = 1; return; }
        fire_gfx_pathsAdd(result, pts, n, 1, kind);
    }
}

typedef struct FireGfxSegment { double first, second; } FireGfxSegment;

static void fire_gfx_hatch(const FireGfxSlicer* sl, const FireGfxGrid* d, double level, double stepPx, fire_gfx_int h, FireGfxToolPaths* paths) {
    fire_gfx_int leftToRight = 1, lastRow = -1;
    double gy;
    FireGfxSegment* segments = NULL;
    fire_gfx_size segCount, segCap = 0;
    for (gy = 1.0; gy <= h; gy += stepPx) {
        fire_gfx_int row = (fire_gfx_int)nearbyint(gy), x;   /* Math.Round: half to even */
        double start = 0;
        fire_gfx_size k;
        if (row == lastRow) continue;
        lastRow = row;
        segCount = 0;
        for (x = 1; x < d->W; x++) {
            double v0 = FIRE_GFX_AT(d, x - 1, row), v1 = FIRE_GFX_AT(d, x, row);
            fire_gfx_int prevIn = v0 >= level, curIn = v1 >= level;
            if (!prevIn && curIn) start = (x - 1) + (level - v0) / (v1 - v0);
            else if (prevIn && !curIn) {
                if (!fire_gfx_grow((void**)&segments, &segCap, segCount, sizeof(FireGfxSegment))) { paths->failed = 1; fire_gfx_free(segments); return; }
                segments[segCount].first = start; segments[segCount].second = (x - 1) + (level - v0) / (v1 - v0);
                segCount++;
            }
        }
        if (segCount == 0) continue;
        if (!leftToRight) {
            for (k = 0; k < segCount / 2; k++) { FireGfxSegment t = segments[k]; segments[k] = segments[segCount - 1 - k]; segments[segCount - 1 - k] = t; }
            for (k = 0; k < segCount; k++) { double t = segments[k].first; segments[k].first = segments[k].second; segments[k].second = t; }
        }
        for (k = 0; k < segCount; k++) {
            FireGfxPointD* pts = (FireGfxPointD*)fire_gfx_malloc(sizeof(FireGfxPointD) * 2);
            if (pts == NULL) { paths->failed = 1; fire_gfx_free(segments); return; }
            pts[0] = fire_gfx_toMm(sl, segments[k].first, row, h);
            pts[1] = fire_gfx_toMm(sl, segments[k].second, row, h);
            fire_gfx_pathsAdd(paths, pts, 2, 0, FIRE_GFX_PATH_FILL);
        }
        leftToRight = !leftToRight;
    }
    fire_gfx_free(segments);
}

static void fire_gfx_sliceMask(const FireGfxSlicer* sl, const char* mask, fire_gfx_int w, fire_gfx_int h, FireGfxToolPaths* result) {
    FireGfxGrid dist;
    double radiusPx = sl->lineWidth / 2.0 / sl->pixelSize;
    double stepPx = fire_gfx_slicerStepOver(sl) / sl->pixelSize;
    double baseLevel = radiusPx + 0.5;   /* distances are measured from pixel center to pixel center: the real edge is half a pixel further out */
    double maxDist = 0;
    fire_gfx_size i, total;
    FireGfxContours contours = {NULL, 0, 0};
    FireGfxToolPaths outline = {NULL, 0, 0, 0};
    if (!fire_gfx_distanceField(mask, w, h, &dist)) { result->failed = 1; return; }
    total = (fire_gfx_size)dist.W * dist.H;
    for (i = 0; i < total; i++) if (dist.v[i] > maxDist) maxDist = dist.v[i];
    if (baseLevel > maxDist) { fire_gfx_free(dist.v); return; }   /* no place is wide enough for the line width */
    if (!fire_gfx_traceContours(&dist, baseLevel, &contours)) result->failed = 1;
    fire_gfx_toPaths(sl, &contours, h, FIRE_GFX_PATH_OUTLINE, &outline);
    fire_gfx_contoursFree(&contours);
    if (sl->strategy == FIRE_GFX_STRATEGY_CONTOUR) {
        FireGfxToolPaths rings = {NULL, 0, 0, 0};
        double level;
        for (level = baseLevel + stepPx; level < maxDist; level += stepPx) {
            if (!fire_gfx_traceContours(&dist, level, &contours)) rings.failed = 1;
            fire_gfx_toPaths(sl, &contours, h, FIRE_GFX_PATH_FILL, &rings);
            fire_gfx_contoursFree(&contours);
        }
        /* start inside, work outwards */
        for (i = rings.count; i > 0; i--) fire_gfx_pathsAdd(result, rings.items[i - 1].points, rings.items[i - 1].count, rings.items[i - 1].closed, rings.items[i - 1].kind);
        if (rings.failed) result->failed = 1;
        fire_gfx_free(rings.items);
    } else if (sl->strategy == FIRE_GFX_STRATEGY_ZIGZAG) fire_gfx_hatch(sl, &dist, baseLevel, stepPx, h, result);
    for (i = 0; i < outline.count; i++) fire_gfx_pathsAdd(result, outline.items[i].points, outline.items[i].count, outline.items[i].closed, outline.items[i].kind);   /* the edge contour last, as the finishing path */
    if (outline.failed) result->failed = 1;
    fire_gfx_free(outline.items);
    fire_gfx_free(dist.v);
}

/* The tool paths of a framebuffer (a pixel that is set: a palette index other than 0, or a color that is not black and not transparent). result->failed: out of memory. */
static void fire_gfx_slice(const FireGfxSlicer* sl, const FireGfxFramebuffer* fb, FireGfxToolPaths* result) {
    fire_gfx_int w = fb->width, h = fb->height, x, y;
    char* mask = (char*)fire_gfx_malloc(w && h ? (fire_gfx_size)w * h : 1);
    if (mask == NULL) { result->failed = 1; return; }
    fire_gfx_fbCpu(fb, 0);
    for (y = 0; y < h; y++)
        for (x = 0; x < w; x++) {
            fire_gfx_size i = (fire_gfx_size)y * w + x;
            mask[(fire_gfx_size)x * h + y] = fb->indices ? fb->indices[i] != 0 : ((fb->pixels[i] & 0x00FFFFFFu) != 0 && (fb->pixels[i] >> 24) != 0);   /* mask[x][y] */
        }
    fire_gfx_sliceMask(sl, mask, w, h, result);
    fire_gfx_free(mask);
}

#endif
