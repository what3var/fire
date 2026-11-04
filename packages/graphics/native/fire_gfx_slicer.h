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
typedef struct FireGfxToolPath { FireGfxPointD* points; size_t count; int closed; int kind; } FireGfxToolPath;
typedef struct FireGfxToolPaths { FireGfxToolPath* items; size_t count, cap; int failed; } FireGfxToolPaths;

static void fire_gfx_pathsFree(FireGfxToolPaths* p) {
    size_t i;
    for (i = 0; i < p->count; i++) free(p->items[i].points);
    free(p->items);
    p->items = NULL; p->count = 0; p->cap = 0;
}
/* Appends a path (its points are taken over). */
static void fire_gfx_pathsAdd(FireGfxToolPaths* p, FireGfxPointD* points, size_t count, int closed, int kind) {
    if (!fire_gfx_grow((void**)&p->items, &p->cap, p->count, sizeof(FireGfxToolPath))) { free(points); p->failed = 1; return; }
    p->items[p->count].points = points; p->items[p->count].count = count; p->items[p->count].closed = closed; p->items[p->count].kind = kind;
    p->count++;
}

typedef struct FireGfxSlicer {
    double lineWidth, pixelSize, overlap, simplifyTolerance;   /* simplifyTolerance: NaN = a quarter of a pixel */
    int strategy;
    int flipY;
} FireGfxSlicer;

static void fire_gfx_slicerInit(FireGfxSlicer* s) { s->lineWidth = 0; s->pixelSize = 0; s->overlap = 0.5; s->simplifyTolerance = 0; s->strategy = FIRE_GFX_STRATEGY_CONTOUR; s->flipY = 1; }
static double fire_gfx_slicerStepOver(const FireGfxSlicer* s) { return s->lineWidth * (1.0 - s->overlap); }

/* A grid indexed [x][y], x major. */
typedef struct FireGfxGrid { int W, H; double* v; } FireGfxGrid;
#define FIRE_GFX_AT(g, x, y) ((g)->v[(size_t)(x) * (g)->H + (y)])

static void fire_gfx_edt1d(const double* f, int n, double* d, int* v, double* z) {
    int k = 0, q;
    v[0] = 0;
    z[0] = -INFINITY;
    z[1] = INFINITY;
    for (q = 1; q < n; q++) {
        double s = ((f[(size_t)q] + (double)q * q) - (f[(size_t)v[(size_t)k]] + (double)v[(size_t)k] * v[(size_t)k])) / (2.0 * q - 2.0 * v[(size_t)k]);
        while (s <= z[(size_t)k]) {
            k--;
            s = ((f[(size_t)q] + (double)q * q) - (f[(size_t)v[(size_t)k]] + (double)v[(size_t)k] * v[(size_t)k])) / (2.0 * q - 2.0 * v[(size_t)k]);
        }
        k++;
        v[(size_t)k] = q;
        z[(size_t)k] = s;
        z[(size_t)k + 1] = INFINITY;
    }
    k = 0;
    for (q = 0; q < n; q++) {
        double dq;
        while (z[(size_t)k + 1] < q) k++;
        dq = q - v[(size_t)k];
        d[(size_t)q] = dq * dq + f[(size_t)v[(size_t)k]];
    }
}

/* 0 without memory. */
static int fire_gfx_distanceField(const char* mask, int w, int h, FireGfxGrid* d) {
    double inf;
    int n, x, y;
    double* f;
    double* outp;
    double* z;
    int* v;
    d->W = w + 2; d->H = h + 2;
    d->v = (double*)calloc((size_t)d->W * d->H, sizeof(double));
    if (d->v == NULL) return 0;
    inf = (double)(d->W + d->H) * (d->W + d->H);
    for (y = 0; y < d->H; y++)
        for (x = 0; x < d->W; x++) {
            int inside = x >= 1 && x <= w && y >= 1 && y <= h && mask[(size_t)(x - 1) * h + (y - 1)];
            FIRE_GFX_AT(d, x, y) = inside ? inf : 0.0;
        }
    n = fire_gfx_imax(d->W, d->H);
    f = (double*)calloc((size_t)n, sizeof(double));
    outp = (double*)calloc((size_t)n, sizeof(double));
    z = (double*)calloc((size_t)n + 1, sizeof(double));
    v = (int*)calloc((size_t)n, sizeof(int));
    if (f == NULL || outp == NULL || z == NULL || v == NULL) { free(f); free(outp); free(z); free(v); free(d->v); d->v = NULL; return 0; }
    for (x = 0; x < d->W; x++) {
        for (y = 0; y < d->H; y++) f[(size_t)y] = FIRE_GFX_AT(d, x, y);
        fire_gfx_edt1d(f, d->H, outp, v, z);
        for (y = 0; y < d->H; y++) FIRE_GFX_AT(d, x, y) = outp[(size_t)y];
    }
    for (y = 0; y < d->H; y++) {
        for (x = 0; x < d->W; x++) f[(size_t)x] = FIRE_GFX_AT(d, x, y);
        fire_gfx_edt1d(f, d->W, outp, v, z);
        for (x = 0; x < d->W; x++) FIRE_GFX_AT(d, x, y) = sqrt(outp[(size_t)x]);
    }
    free(f); free(outp); free(z); free(v);
    return 1;
}

/* ---- marching squares: the edges of the cells that the level crosses, linked to neighbours; every edge has at most two (one per cell it belongs to) ---- */
typedef struct FireGfxEdge { int64_t key; int64_t nb[2]; int count; int visited; } FireGfxEdge;
typedef struct FireGfxEdges {
    FireGfxEdge* items;   /* in the order they were added (the iteration order of the VM's dictionary) */
    size_t count, cap;
    int32_t* slots;       /* open addressing: index + 1 into `items`, 0 free */
    size_t mask;
    int failed;
} FireGfxEdges;

static size_t fire_gfx_edgeHash(int64_t key, size_t mask) { return (size_t)(((uint64_t)key * 0x9E3779B97F4A7C15ull) >> 32) & mask; }
static FireGfxEdge* fire_gfx_edgeFind(const FireGfxEdges* e, int64_t key) {
    size_t at;
    if (e->slots == NULL) return NULL;
    at = fire_gfx_edgeHash(key, e->mask);
    while (e->slots[at] != 0) {
        FireGfxEdge* x = &e->items[e->slots[at] - 1];
        if (x->key == key) return x;
        at = (at + 1) & e->mask;
    }
    return NULL;
}
static int fire_gfx_edgeRehash(FireGfxEdges* e, size_t slots) {
    int32_t* table = (int32_t*)calloc(slots, sizeof(int32_t));
    size_t i;
    if (table == NULL) return 0;
    free(e->slots);
    e->slots = table;
    e->mask = slots - 1;
    for (i = 0; i < e->count; i++) {
        size_t at = fire_gfx_edgeHash(e->items[i].key, e->mask);
        while (e->slots[at] != 0) at = (at + 1) & e->mask;
        e->slots[at] = (int32_t)(i + 1);
    }
    return 1;
}
/* adj[from].push_back(to) */
static void fire_gfx_edgeHalf(FireGfxEdges* e, int64_t from, int64_t to) {
    FireGfxEdge* x = fire_gfx_edgeFind(e, from);
    if (x == NULL) {
        size_t at;
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

typedef struct FireGfxContour { FireGfxPointD* points; size_t count, cap; } FireGfxContour;
typedef struct FireGfxContours { FireGfxContour* items; size_t count, cap; } FireGfxContours;
static void fire_gfx_contoursFree(FireGfxContours* c) { size_t i; for (i = 0; i < c->count; i++) free(c->items[i].points); free(c->items); c->items = NULL; c->count = c->cap = 0; }

static FireGfxPointD fire_gfx_edgePoint(const FireGfxGrid* d, int64_t key, double level) {
    int64_t cell = key >> 1;
    int x = (int)(cell % d->W), y = (int)(cell / d->W);
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

static int fire_gfx_traceContours(const FireGfxGrid* d, double level, FireGfxContours* contours) {
    int W = d->W, H = d->H, x, y;
    FireGfxEdges adj;
    size_t s;
    memset(&adj, 0, sizeof adj);
    for (y = 0; y < H - 1; y++)
        for (x = 0; x < W - 1; x++) {
            double a = FIRE_GFX_AT(d, x, y), b = FIRE_GFX_AT(d, x + 1, y), c = FIRE_GFX_AT(d, x + 1, y + 1), e = FIRE_GFX_AT(d, x, y + 1);
            int idx = (a >= level ? 8 : 0) | (b >= level ? 4 : 0) | (c >= level ? 2 : 0) | (e >= level ? 1 : 0);
            int64_t T, R, B, L;
            int centerIn;
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
    if (adj.failed) { free(adj.items); free(adj.slots); return 0; }
    for (s = 0; s < adj.count; s++) {
        int64_t start = adj.items[s].key, prev = -1, cur = start;
        FireGfxContour poly = {NULL, 0, 0};
        if (adj.items[s].visited) continue;
        for (;;) {
            FireGfxEdge* node = fire_gfx_edgeFind(&adj, cur);
            FireGfxEdge* nextNode;
            int64_t next;
            node->visited = 1;
            if (!fire_gfx_grow((void**)&poly.points, &poly.cap, poly.count, sizeof(FireGfxPointD))) { free(poly.points); free(adj.items); free(adj.slots); return 0; }
            poly.points[poly.count++] = fire_gfx_edgePoint(d, cur, level);
            next = node->nb[0] != prev ? node->nb[0] : node->count > 1 ? node->nb[1] : -1;
            if (next == -1 || next == start) break;
            nextNode = fire_gfx_edgeFind(&adj, next);
            if (nextNode == NULL || nextNode->visited) break;
            prev = cur;
            cur = next;
        }
        if (poly.count >= 2) {
            if (!fire_gfx_grow((void**)&contours->items, &contours->cap, contours->count, sizeof(FireGfxContour))) { free(poly.points); free(adj.items); free(adj.slots); return 0; }
            contours->items[contours->count++] = poly;
        } else free(poly.points);
    }
    free(adj.items);
    free(adj.slots);
    return 1;
}

static FireGfxPointD fire_gfx_toMm(const FireGfxSlicer* sl, double gx, double gy, int h) {
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
static FireGfxPointD* fire_gfx_simplify(FireGfxPointD* pts, size_t* count, double tol) {
    size_t n = *count, i, kept = 0, top = 0;
    FireGfxPointD* work;
    char* keep;
    int* stack;   /* pairs s, e */
    if (tol <= 0 || n < 4) return pts;
    work = (FireGfxPointD*)malloc(sizeof(FireGfxPointD) * (n + 1));
    keep = (char*)calloc(n + 1, 1);
    stack = (int*)malloc(sizeof(int) * 2 * (n + 1));
    if (work == NULL || keep == NULL || stack == NULL) { free(work); free(keep); free(stack); free(pts); return NULL; }
    memcpy(work, pts, sizeof(FireGfxPointD) * n);
    work[n] = pts[0];
    keep[0] = keep[n] = 1;
    stack[0] = 0; stack[1] = (int)n; top = 1;
    while (top > 0) {
        int s, e, index = -1, k;
        double maxDist = 0;
        top--;
        s = stack[2 * top]; e = stack[2 * top + 1];
        for (k = s + 1; k < e; k++) {
            double dist = fire_gfx_segmentDistance(work[(size_t)k], work[(size_t)s], work[(size_t)e]);
            if (dist > maxDist) { maxDist = dist; index = k; }
        }
        if (index >= 0 && maxDist > tol) {
            keep[(size_t)index] = 1;
            stack[2 * top] = s; stack[2 * top + 1] = index; top++;
            stack[2 * top] = index; stack[2 * top + 1] = e; top++;
        }
    }
    /* (the stack never holds more pairs than points: every pair that is pushed splits a range at a point that is kept) */
    for (i = 0; i < n + 1; i++) if (keep[i]) work[kept++] = work[i];
    kept--;   /* (the repeated first point) */
    free(keep);
    free(stack);
    if (kept >= 2) { free(pts); *count = kept; return work; }
    free(work);
    return pts;
}

static void fire_gfx_toPaths(const FireGfxSlicer* sl, const FireGfxContours* contours, int h, int kind, FireGfxToolPaths* result) {
    double tol = isnan(sl->simplifyTolerance) ? sl->pixelSize * 0.25 : sl->simplifyTolerance;
    size_t c, i;
    for (c = 0; c < contours->count; c++) {
        const FireGfxContour* contour = &contours->items[c];
        size_t n = contour->count;
        FireGfxPointD* pts = (FireGfxPointD*)malloc(sizeof(FireGfxPointD) * (n ? n : 1));
        if (pts == NULL) { result->failed = 1; return; }
        for (i = 0; i < n; i++) pts[i] = fire_gfx_toMm(sl, contour->points[i].x, contour->points[i].y, h);
        pts = fire_gfx_simplify(pts, &n, tol);
        if (pts == NULL) { result->failed = 1; return; }
        fire_gfx_pathsAdd(result, pts, n, 1, kind);
    }
}

typedef struct FireGfxSegment { double first, second; } FireGfxSegment;

static void fire_gfx_hatch(const FireGfxSlicer* sl, const FireGfxGrid* d, double level, double stepPx, int h, FireGfxToolPaths* paths) {
    int leftToRight = 1, lastRow = -1;
    double gy;
    FireGfxSegment* segments = NULL;
    size_t segCount, segCap = 0;
    for (gy = 1.0; gy <= h; gy += stepPx) {
        int row = (int)nearbyint(gy), x;   /* Math.Round: half to even */
        double start = 0;
        size_t k;
        if (row == lastRow) continue;
        lastRow = row;
        segCount = 0;
        for (x = 1; x < d->W; x++) {
            double v0 = FIRE_GFX_AT(d, x - 1, row), v1 = FIRE_GFX_AT(d, x, row);
            int prevIn = v0 >= level, curIn = v1 >= level;
            if (!prevIn && curIn) start = (x - 1) + (level - v0) / (v1 - v0);
            else if (prevIn && !curIn) {
                if (!fire_gfx_grow((void**)&segments, &segCap, segCount, sizeof(FireGfxSegment))) { paths->failed = 1; free(segments); return; }
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
            FireGfxPointD* pts = (FireGfxPointD*)malloc(sizeof(FireGfxPointD) * 2);
            if (pts == NULL) { paths->failed = 1; free(segments); return; }
            pts[0] = fire_gfx_toMm(sl, segments[k].first, row, h);
            pts[1] = fire_gfx_toMm(sl, segments[k].second, row, h);
            fire_gfx_pathsAdd(paths, pts, 2, 0, FIRE_GFX_PATH_FILL);
        }
        leftToRight = !leftToRight;
    }
    free(segments);
}

static void fire_gfx_sliceMask(const FireGfxSlicer* sl, const char* mask, int w, int h, FireGfxToolPaths* result) {
    FireGfxGrid dist;
    double radiusPx = sl->lineWidth / 2.0 / sl->pixelSize;
    double stepPx = fire_gfx_slicerStepOver(sl) / sl->pixelSize;
    double baseLevel = radiusPx + 0.5;   /* distances are measured from pixel center to pixel center: the real edge is half a pixel further out */
    double maxDist = 0;
    size_t i, total;
    FireGfxContours contours = {NULL, 0, 0};
    FireGfxToolPaths outline = {NULL, 0, 0, 0};
    if (!fire_gfx_distanceField(mask, w, h, &dist)) { result->failed = 1; return; }
    total = (size_t)dist.W * dist.H;
    for (i = 0; i < total; i++) if (dist.v[i] > maxDist) maxDist = dist.v[i];
    if (baseLevel > maxDist) { free(dist.v); return; }   /* no place is wide enough for the line width */
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
        free(rings.items);
    } else if (sl->strategy == FIRE_GFX_STRATEGY_ZIGZAG) fire_gfx_hatch(sl, &dist, baseLevel, stepPx, h, result);
    for (i = 0; i < outline.count; i++) fire_gfx_pathsAdd(result, outline.items[i].points, outline.items[i].count, outline.items[i].closed, outline.items[i].kind);   /* the edge contour last, as the finishing path */
    if (outline.failed) result->failed = 1;
    free(outline.items);
    free(dist.v);
}

/* The tool paths of a framebuffer (a pixel that is set: a palette index other than 0, or a color that is not black and not transparent). result->failed: out of memory. */
static void fire_gfx_slice(const FireGfxSlicer* sl, const FireGfxFramebuffer* fb, FireGfxToolPaths* result) {
    int w = fb->width, h = fb->height, x, y;
    char* mask = (char*)malloc(w && h ? (size_t)w * h : 1);
    if (mask == NULL) { result->failed = 1; return; }
    fire_gfx_fbCpu(fb, 0);
    for (y = 0; y < h; y++)
        for (x = 0; x < w; x++) {
            size_t i = (size_t)y * w + x;
            mask[(size_t)x * h + y] = fb->indices ? fb->indices[i] != 0 : ((fb->pixels[i] & 0x00FFFFFFu) != 0 && (fb->pixels[i] >> 24) != 0);   /* mask[x][y] */
        }
    fire_gfx_sliceMask(sl, mask, w, h, result);
    free(mask);
}

#endif
