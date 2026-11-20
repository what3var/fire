/* native/forge/model/modelconverter.c - fire's model file (.f3d, docs/graphics/models.md) from glTF 2.0 (Phase D 4): src/fire.Compiler/ModelConverter.cs
 * (commit 6c1b0f0e), 1:1 - Convert (the buffers, the accessors, the nodes parent before child, the meshes, the clips sampled at FramesPerSecond), Write
 * (the bytes of the file), the helpers of the poses (QuatOf, PoseMatrix, Mul, Decompose). BlockModel.cs and MeshModel.cs (model/blockmodel.c,
 * model/meshmodel.c) write their models through the same Write.
 *
 * What the C# code gets from .NET is written out here: the JsonElement operations with their exceptions (the type and the message of .NET - a
 * default element, a wrong kind, a missing key, an index past the end, a number that is no int), Math.Round (to even), the saturating conversions of
 * .NET 9+ (double -> int: NaN 0), Math.Min/Max (NaN, -0), Enumerable.Min/Max over doubles, Array.BinarySearch, Convert.FromBase64String,
 * Uri.UnescapeDataString. An exception is an FcError of kind FC_ERR_INTERNAL with the name of the .NET exception in `file` and its message (mdThrow);
 * mdConvertFile catches it and hands both to the caller - which decides as C# does whether it is one it catches (mdCaught). */

#include <float.h>

#define MD_FPS 30   /* ModelConverter.FramesPerSecond */

/* ---- the exceptions of .NET ---- */
static _Noreturn void mdThrow(FcCtx* c, const char* type, const char* message) { fcThrow(c, FC_ERR_INTERNAL, 0, 0, type, message); }
static _Noreturn void mdInvalidData(FcCtx* c, const char* message) { mdThrow(c, "System.IO.InvalidDataException", message); }
static _Noreturn void mdIndexOut(FcCtx* c) { mdThrow(c, "System.IndexOutOfRangeException", "Index was outside the bounds of the array."); }
static _Noreturn void mdListIndexOut(FcCtx* c) {
    mdThrow(c, "System.ArgumentOutOfRangeException", "Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index')");
}
static _Noreturn void mdFormat(FcCtx* c) { mdThrow(c, "System.FormatException", "One of the identified items was in an invalid format."); }
/* the exceptions the callers of ConvertFile catch (Program.cs `model`, ResourceTable.Add): IOException (and FileNotFoundException, ...), InvalidDataException,
 * FormatException, KeyNotFoundException, InvalidOperationException, IndexOutOfRangeException */
static bool mdCaught(const char* type) {
    return type && (strcmp(type, "System.IO.InvalidDataException") == 0 || strcmp(type, "System.IO.IOException") == 0 || strcmp(type, "System.IO.FileNotFoundException") == 0
        || strcmp(type, "System.IO.DirectoryNotFoundException") == 0 || strcmp(type, "System.FormatException") == 0 || strcmp(type, "System.Collections.Generic.KeyNotFoundException") == 0
        || strcmp(type, "System.InvalidOperationException") == 0 || strcmp(type, "System.IndexOutOfRangeException") == 0);
}

/* ---- JsonElement (a NULL value is default(JsonElement), ValueKind Undefined) ---- */
static const char* mdKindName(const FjValue* v) {
    if (!v) return "Undefined";
    switch (v->kind) {
        case FJK_OBJECT: return "Object";
        case FJK_ARRAY: return "Array";
        case FJK_STRING: return "String";
        case FJK_NUMBER: return "Number";
        case FJK_TRUE: return "True";
        case FJK_FALSE: return "False";
        default: return "Null";
    }
}
static void mdCheckValid(FcCtx* c, const FjValue* v) {
    if (!v) mdThrow(c, "System.InvalidOperationException", "Operation is not valid due to the current state of the object.");
}
static void mdWrongType(FcCtx* c, const char* expected, const FjValue* v) {
    mdThrow(c, "System.InvalidOperationException", fcFmt(c, "The requested operation requires an element of type '%s', but the target element has type '%s'.", expected, mdKindName(v)));
}
/* TryGetProperty */
static FjValue* mdTry(FcCtx* c, const FjValue* v, const char* name) {
    mdCheckValid(c, v);
    if (v->kind != FJK_OBJECT) mdWrongType(c, "Object", v);
    return fjProp(v, name);
}
/* GetProperty */
static FjValue* mdGet(FcCtx* c, const FjValue* v, const char* name) {
    FjValue* p = mdTry(c, v, name);
    if (!p) mdThrow(c, "System.Collections.Generic.KeyNotFoundException", "The given key was not present in the dictionary.");
    return p;
}
/* GetArrayLength, EnumerateArray */
static int mdLen(FcCtx* c, const FjValue* v) {
    mdCheckValid(c, v);
    if (v->kind != FJK_ARRAY) mdWrongType(c, "Array", v);
    return v->items->count;
}
/* the indexer */
static FjValue* mdAt(FcCtx* c, const FjValue* v, int i) {
    int n = mdLen(c, v);
    if ((unsigned)i >= (unsigned)n) mdIndexOut(c);
    return (FjValue*)v->items->items[i];
}
static int mdInt(FcCtx* c, const FjValue* v) {
    int x;
    mdCheckValid(c, v);
    if (v->kind != FJK_NUMBER) mdWrongType(c, "Number", v);
    if (!fjTryGetInt32(v, &x)) mdFormat(c);
    return x;
}
static double mdDouble(FcCtx* c, const FjValue* v) {
    double x;
    mdCheckValid(c, v);
    if (v->kind != FJK_NUMBER) mdWrongType(c, "Number", v);
    if (!fjTryGetDouble(v, &x)) mdFormat(c);
    return x;
}
/* GetString: null for a JSON null */
static const char* mdStr(FcCtx* c, const FjValue* v) {
    mdCheckValid(c, v);
    if (v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_STRING) mdWrongType(c, "String", v);
    return v->text;
}
static bool mdBool(FcCtx* c, const FjValue* v) {
    mdCheckValid(c, v);
    if (v->kind == FJK_TRUE) return true;
    if (v->kind == FJK_FALSE) return false;
    mdWrongType(c, "Boolean", v);
    return false;
}

/* ---- numbers as .NET has them ---- */
static double mdRound(double x) { return nearbyint(x); }   /* Math.Round: to even (the default rounding of the FPU) */
static int mdToInt(double x) {                              /* (int)x, saturating (.NET 9+) */
    if (x != x) return 0;
    if (x >= 2147483647.0) return INT32_MAX;
    if (x <= -2147483648.0) return INT32_MIN;
    return (int)x;
}
static double mdClamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
static int mdClampI(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
static bool mdNegative(double x) { return signbit(x) != 0; }
static double mdMin(double a, double b) { if (a < b || a != a) return a; if (a == b) return mdNegative(a) ? a : b; return b; }
static double mdMax(double a, double b) { if (a > b || a != a) return a; if (a == b) return mdNegative(a) ? b : a; return b; }
/* double.CompareTo (NaN is the smallest) */
static int mdCompare(double a, double b) {
    if (a < b) return -1;
    if (a > b) return 1;
    if (a == b) return 0;
    if (a != a) return b != b ? 0 : -1;
    return 1;
}
static short mdShort(double v) { return (short)mdToInt(mdClamp(mdRound(v), -32768, 32767)); }   /* ModelConverter.Short */

/* ---- vectors (double[] of any length: the arrays of a file are taken as they are) ---- */
typedef struct MdVec { double* v; int n; } MdVec;
static MdVec mdVec(FcCtx* c, int n) { MdVec r; r.n = n; r.v = (double*)fcAlloc(c, sizeof(double) * (size_t)(n > 0 ? n : 1)); return r; }
static MdVec mdVecOf(FcCtx* c, int n, const double* values) { MdVec r = mdVec(c, n); memcpy(r.v, values, sizeof(double) * (size_t)n); return r; }
static double mdV(FcCtx* c, MdVec a, int i) { if (i < 0 || i >= a.n) mdIndexOut(c); return a.v[i]; }
static void mdSetV(FcCtx* c, MdVec a, int i, double x) { if (i < 0 || i >= a.n) mdIndexOut(c); a.v[i] = x; }
/* x.EnumerateArray().Select(e => e.GetDouble()).ToArray() */
static MdVec mdDoubles(FcCtx* c, const FjValue* x) {
    int n = mdLen(c, x), i;
    MdVec r = mdVec(c, n);
    for (i = 0; i < n; i++) r.v[i] = mdDouble(c, (const FjValue*)x->items->items[i]);
    return r;
}

typedef struct MdVecList { MdVec* items; int count, cap; } MdVecList;
static void mdVecAdd(FcCtx* c, MdVecList* l, MdVec v) {
    if (l->count == l->cap) {
        int cap = l->cap ? l->cap * 2 : 16;
        MdVec* items = (MdVec*)fcAlloc(c, sizeof(MdVec) * (size_t)cap);
        if (l->count) memcpy(items, l->items, sizeof(MdVec) * (size_t)l->count);
        l->items = items;
        l->cap = cap;
    }
    l->items[l->count++] = v;
}
typedef struct MdInts { int* items; int count, cap; } MdInts;
static void mdIntAdd(FcCtx* c, MdInts* l, int v) {
    if (l->count == l->cap) {
        int cap = l->cap ? l->cap * 2 : 16;
        int* items = (int*)fcAlloc(c, sizeof(int) * (size_t)cap);
        if (l->count) memcpy(items, l->items, sizeof(int) * (size_t)l->count);
        l->items = items;
        l->cap = cap;
    }
    l->items[l->count++] = v;
}

/* ---- the text of a name: the first n UTF-16 units of an fc8 text (Substring(0, n)) ---- */
static const char* mdUnits(FcCtx* c, const char* s, int n) {
    const unsigned char* p = (const unsigned char*)s;
    int k = 0;
    while (*p && k < n) { fc8Next(&p); k++; }
    return fcStrN(c, s, (size_t)((const char*)p - s));
}

/* ModelConverter.Part: the rest pose relative to the parent, the mesh in the part's space, the colour (RGBA, as many bytes as the file gave) or none */
typedef struct MdPart {
    int parent;
    const char* name;   /* fc8 */
    MdVec t, r, s;
    MdVecList positions, normals, uvs;
    MdInts indices;
    uint8_t* color;     /* NULL: none */
    int colorCount;
    bool hasUv;
} MdPart;
static MdPart* mdPartNew(FcCtx* c) {
    static const double t0[3] = { 0, 0, 0 }, r0[4] = { 0, 0, 0, 1 }, s0[3] = { 1, 1, 1 };
    MdPart* p = (MdPart*)fcAlloc(c, sizeof *p);
    memset(p, 0, sizeof *p);
    p->parent = -1;
    p->name = "";
    p->t = mdVecOf(c, 3, t0);
    p->r = mdVecOf(c, 4, r0);
    p->s = mdVecOf(c, 3, s0);
    return p;
}
/* parts.Add */
static void mdPartPush(FcCtx* c, MdPart*** parts, int* count, int* cap, MdPart* p) {
    if (*count == *cap) {
        int n = *cap ? *cap * 2 : 16;
        MdPart** items = (MdPart**)fcAlloc(c, sizeof(MdPart*) * (size_t)n);
        if (*count) memcpy(items, *parts, sizeof(MdPart*) * (size_t)*count);
        *parts = items;
        *cap = n;
    }
    (*parts)[(*count)++] = p;
}
/* a clip: frames x parts of poses (translation, rotation, scale - 10 numbers, or more where a file gave longer arrays) */
typedef struct MdClip { const char* name; int frames; MdVec** poses; bool loop; } MdClip;

static const uint8_t* mdConvertBlocks(FcCtx* c, const char* text, size_t* n);   /* model/blockmodel.c */
static const uint8_t* mdConvertMesh(FcCtx* c, const char* text, size_t* n);     /* model/meshmodel.c */

/* ---- the bytes of a file (BinaryWriter: little endian) ---- */
static void mdW8(FcBuf* b, uint8_t v) { fcBufPut(b, (const char*)&v, 1); }
static void mdW16(FcBuf* b, uint16_t v) { uint8_t t[2]; t[0] = (uint8_t)v; t[1] = (uint8_t)(v >> 8); fcBufPut(b, (const char*)t, 2); }
static void mdW32(FcBuf* b, uint32_t v) { uint8_t t[4]; t[0] = (uint8_t)v; t[1] = (uint8_t)(v >> 8); t[2] = (uint8_t)(v >> 16); t[3] = (uint8_t)(v >> 24); fcBufPut(b, (const char*)t, 4); }
static void mdWF(FcBuf* b, double v) { float f = (float)v; uint32_t u; memcpy(&u, &f, 4); mdW32(b, u); }

static void mdWriteName(FcBuf* w, const char* name) {
    FcBuf b = {0};
    fcBufStr(&b, "");
    fcOut8(&b, name);   /* Encoding.UTF8.GetBytes */
    if (b.len > 31) b.len = 31;
    mdW8(w, (uint8_t)b.len);
    fcBufPut(w, b.p, b.len);
    fcBufFree(&b);
}
static void mdWriteQuat(FcCtx* c, FcBuf* w, MdVec q) {
    static const double one[4] = { 0, 0, 0, 1 };
    double l = sqrt(mdV(c, q, 0) * mdV(c, q, 0) + mdV(c, q, 1) * mdV(c, q, 1) + mdV(c, q, 2) * mdV(c, q, 2) + mdV(c, q, 3) * mdV(c, q, 3));
    int k;
    if (l < 1e-12) { q = mdVecOf(c, 4, one); l = 1; }
    for (k = 0; k < 4; k++) mdW16(w, (uint16_t)mdShort(q.v[k] / l * 32767));
}
/* OctNormal: a normal in two signed bytes */
static void mdOctNormal(FcCtx* c, MdVec n, int8_t* a, int8_t* b) {
    double x = mdV(c, n, 0), y = mdV(c, n, 1), z = mdV(c, n, 2), l = fabs(x) + fabs(y) + fabs(z);
    if (l < 1e-12) { *a = 0; *b = 127; return; }
    x /= l; y /= l;
    if (z < 0) { double ox = x; x = (1 - fabs(y)) * (ox >= 0 ? 1 : -1); y = (1 - fabs(ox)) * (y >= 0 ? 1 : -1); }
    *a = (int8_t)mdToInt(mdClamp(mdRound(x * 127), -127, 127));
    *b = (int8_t)mdToInt(mdClamp(mdRound(y * 127), -127, 127));
}

/* QuatOf: the quaternion (x, y, z, w) of a rotation matrix (row major 3x3 in the first entries of a 4x4) */
static void mdQuatOf(const double* m, double* q) {
    double r00 = m[0], r01 = m[1], r02 = m[2], r10 = m[4], r11 = m[5], r12 = m[6], r20 = m[8], r21 = m[9], r22 = m[10], tr = r00 + r11 + r22;
    if (tr > 0) { double s = sqrt(tr + 1) * 2; q[0] = (r21 - r12) / s; q[1] = (r02 - r20) / s; q[2] = (r10 - r01) / s; q[3] = s / 4; return; }
    if (r00 > r11 && r00 > r22) { double s = sqrt(1 + r00 - r11 - r22) * 2; q[0] = s / 4; q[1] = (r01 + r10) / s; q[2] = (r02 + r20) / s; q[3] = (r21 - r12) / s; return; }
    if (r11 > r22) { double s = sqrt(1 + r11 - r00 - r22) * 2; q[0] = (r01 + r10) / s; q[1] = s / 4; q[2] = (r12 + r21) / s; q[3] = (r02 - r20) / s; return; }
    { double s = sqrt(1 + r22 - r00 - r11) * 2; q[0] = (r02 + r20) / s; q[1] = (r12 + r21) / s; q[2] = s / 4; q[3] = (r10 - r01) / s; }
}

/* PoseMatrix: row major 4x4 (applied to column vectors) of T * R(q) * S */
static void mdPoseMatrix(FcCtx* c, MdVec t, MdVec q, MdVec s, double* m) {
    double l = sqrt(mdV(c, q, 0) * mdV(c, q, 0) + mdV(c, q, 1) * mdV(c, q, 1) + mdV(c, q, 2) * mdV(c, q, 2) + mdV(c, q, 3) * mdV(c, q, 3));
    double x = q.v[0] / l, y = q.v[1] / l, z = q.v[2] / l, w = q.v[3] / l;
    double s0 = mdV(c, s, 0), s1 = mdV(c, s, 1), s2 = mdV(c, s, 2), t0 = mdV(c, t, 0), t1 = mdV(c, t, 1), t2 = mdV(c, t, 2);
    m[0] = (1 - 2 * (y * y + z * z)) * s0; m[1] = 2 * (x * y - z * w) * s1; m[2] = 2 * (x * z + y * w) * s2; m[3] = t0;
    m[4] = 2 * (x * y + z * w) * s0; m[5] = (1 - 2 * (x * x + z * z)) * s1; m[6] = 2 * (y * z - x * w) * s2; m[7] = t1;
    m[8] = 2 * (x * z - y * w) * s0; m[9] = 2 * (y * z + x * w) * s1; m[10] = (1 - 2 * (x * x + y * y)) * s2; m[11] = t2;
    m[12] = 0; m[13] = 0; m[14] = 0; m[15] = 1;
}
static void mdMul(const double* a, const double* b, double* r) {
    int i, j, k;
    for (i = 0; i < 4; i++) for (j = 0; j < 4; j++) { double s = 0; for (k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j]; r[i * 4 + j] = s; }
}

/* Write: the bytes of an .f3d - the parts (parent before child) and the clips */
static const uint8_t* mdWrite(FcCtx* c, MdPart** parts, int partCount, MdClip* clips, int clipCount, int fps, size_t* size) {
    double* world = (double*)fcAlloc(c, sizeof(double) * 16 * (size_t)(partCount > 0 ? partCount : 1));
    double lo[3] = { DBL_MAX, DBL_MAX, DBL_MAX }, hi[3] = { -DBL_MAX, -DBL_MAX, -DBL_MAX };
    FcBuf w = {0};
    int i, k, f, j;
    /* the box of the whole model in its rest pose */
    for (i = 0; i < partCount; i++) {
        double m[16];
        int pi;
        mdPoseMatrix(c, parts[i]->t, parts[i]->r, parts[i]->s, m);
        if (parts[i]->parent >= 0) {
            if (parts[i]->parent >= i) mdIndexOut(c);   /* (world[parent] of a later part is null in C#: never, the parents come first) */
            mdMul(world + 16 * parts[i]->parent, m, world + 16 * i);
        } else memcpy(world + 16 * i, m, sizeof m);
        for (pi = 0; pi < parts[i]->positions.count; pi++) {
            MdVec p = parts[i]->positions.items[pi];
            double* wm = world + 16 * i;
            for (k = 0; k < 3; k++) {
                double v = wm[k * 4] * mdV(c, p, 0) + wm[k * 4 + 1] * mdV(c, p, 1) + wm[k * 4 + 2] * mdV(c, p, 2) + wm[k * 4 + 3];
                lo[k] = mdMin(lo[k], v); hi[k] = mdMax(hi[k], v);
            }
        }
    }
    if (lo[0] > hi[0]) { for (k = 0; k < 3; k++) lo[k] = hi[k] = 0; }

    fcBufPut(&w, "F3D1", 4);
    mdW16(&w, 1); mdW16(&w, (uint16_t)partCount); mdW16(&w, (uint16_t)clipCount); mdW16(&w, 0);
    for (k = 0; k < 3; k++) mdWF(&w, lo[k]);
    for (k = 0; k < 3; k++) mdWF(&w, hi[k]);
    for (i = 0; i < partCount; i++) {
        MdPart* p = parts[i];
        static const uint8_t white[4] = { 255, 255, 255, 255 };
        const uint8_t* col = p->color ? p->color : white;
        int colN = p->color ? p->colorCount : 4;
        double plo[3] = { 0, 0, 0 }, phi[3] = { 0, 0, 0 };
        mdW16(&w, (uint16_t)(short)p->parent);
        mdW8(&w, (uint8_t)((p->color ? 1 : 0) | (p->hasUv ? 2 : 0)));
        mdWriteName(&w, p->name);
        for (k = 0; k < p->t.n; k++) mdWF(&w, p->t.v[k]);
        mdWriteQuat(c, &w, p->r);
        for (k = 0; k < p->s.n; k++) mdWF(&w, p->s.v[k]);
        if (colN < 3) mdIndexOut(c);
        mdW8(&w, col[0]); mdW8(&w, col[1]); mdW8(&w, col[2]); mdW8(&w, colN > 3 ? col[3] : 255);
        mdW16(&w, (uint16_t)p->positions.count); mdW32(&w, (uint32_t)p->indices.count);
        if (p->positions.count > 0)
            for (k = 0; k < 3; k++) {
                /* Enumerable.Min / Max over the k-th coordinates (NaN: Min returns it, Max skips it) */
                double mn = mdV(c, p->positions.items[0], k), mx;
                if (mn == mn)
                    for (j = 1; j < p->positions.count; j++) {
                        double x = mdV(c, p->positions.items[j], k);
                        if (x < mn) mn = x; else if (x != x) { mn = x; break; }
                    }
                plo[k] = mn;
                j = 0;
                mx = mdV(c, p->positions.items[0], k);
                while (mx != mx) { if (++j >= p->positions.count) break; mx = mdV(c, p->positions.items[j], k); }
                for (j = j + 1; j < p->positions.count; j++) { double x = mdV(c, p->positions.items[j], k); if (x > mx) mx = x; }
                phi[k] = mx;
            }
        for (k = 0; k < 3; k++) mdWF(&w, plo[k]);
        for (k = 0; k < 3; k++) mdWF(&w, phi[k]);
        for (j = 0; j < p->positions.count; j++) {
            int8_t a, b;
            for (k = 0; k < 3; k++) {
                /* the decoder reads min + (q + 32768) / 65535 * size, with the box as floats */
                float flo = (float)plo[k], fsize = (float)phi[k] - flo;
                double q = fsize > 0 ? mdRound((double)(((float)mdV(c, p->positions.items[j], k) - flo) / fsize) * 65535.0) - 32768 : -32768;
                mdW16(&w, (uint16_t)(short)mdToInt(mdClamp(q, -32768, 32767)));
            }
            if (j >= p->normals.count) mdListIndexOut(c);
            mdOctNormal(c, p->normals.items[j], &a, &b);
            mdW8(&w, (uint8_t)a); mdW8(&w, (uint8_t)b);
            if (p->hasUv) {
                if (j >= p->uvs.count) mdListIndexOut(c);
                mdW16(&w, (uint16_t)mdShort(mdV(c, p->uvs.items[j], 0) * 1024));
                mdW16(&w, (uint16_t)mdShort(mdV(c, p->uvs.items[j], 1) * 1024));
            }
        }
        for (j = 0; j < p->indices.count; j++) mdW16(&w, (uint16_t)p->indices.items[j]);
    }
    for (i = 0; i < clipCount; i++) {
        MdClip* cl = &clips[i];
        mdWriteName(&w, cl->name);
        mdW8(&w, (uint8_t)(cl->loop ? 1 : 0));
        mdW16(&w, (uint16_t)cl->frames);
        mdWF(&w, (double)(float)fps);
        for (f = 0; f < cl->frames; f++)
            for (j = 0; j < partCount; j++) {
                MdVec pose = cl->poses[f][j], q;
                for (k = 0; k < 3; k++) mdWF(&w, mdV(c, pose, k));
                if (pose.n < 7) mdThrow(c, "System.ArgumentOutOfRangeException", "Specified argument was out of the range of valid values. (Parameter 'length')");
                q.v = pose.v + 3; q.n = 4;
                mdWriteQuat(c, &w, q);
                for (k = 7; k < 10; k++) mdW16(&w, (uint16_t)mdShort(mdV(c, pose, k) * 4096));
            }
    }
    *size = w.len;
    return (const uint8_t*)fcBufDone(c, &w);
}

static void mdSmoothNormals(FcCtx* c, MdPart* p, int baseIndex, const int* idx, int n) {
    int t, j;
    for (t = 0; t + 2 < n; t += 3) {
        MdVec a = p->positions.items[baseIndex + idx[t]], b = p->positions.items[baseIndex + idx[t + 1]], cc = p->positions.items[baseIndex + idx[t + 2]];
        double ux = mdV(c, b, 0) - mdV(c, a, 0), uy = mdV(c, b, 1) - mdV(c, a, 1), uz = mdV(c, b, 2) - mdV(c, a, 2);
        double vx = mdV(c, cc, 0) - mdV(c, a, 0), vy = mdV(c, cc, 1) - mdV(c, a, 1), vz = mdV(c, cc, 2) - mdV(c, a, 2);
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        int ks[3];
        ks[0] = idx[t]; ks[1] = idx[t + 1]; ks[2] = idx[t + 2];
        for (j = 0; j < 3; j++) { MdVec nn = p->normals.items[baseIndex + ks[j]]; nn.v[0] += nx; nn.v[1] += ny; nn.v[2] += nz; }
    }
}

/* Array.BinarySearch */
static int mdBinarySearch(const double* a, int n, double value) {
    int lo = 0, hi = n - 1;
    while (lo <= hi) {
        int i = lo + ((hi - lo) >> 1);
        int order = mdCompare(a[i], value);
        if (order == 0) return i;
        if (order < 0) lo = i + 1; else hi = i - 1;
    }
    return ~lo;
}
static MdVec mdSampleValue(FcCtx* c, MdVecList* values, bool cubic, int i) {
    int at = cubic ? i * 3 + 1 : i;
    if (at < 0 || at >= values->count) mdIndexOut(c);
    return values->items[at];
}
static MdVec mdSample(FcCtx* c, const double* times, int nt, MdVecList* values, const char* interp, double time, bool rotation) {
    bool cubic = strcmp(interp, "CUBICSPLINE") == 0;
    int k, i;
    double f;
    MdVec a, b, r;
    if (time <= times[0]) return mdSampleValue(c, values, cubic, 0);
    if (time >= times[nt - 1]) return mdSampleValue(c, values, cubic, nt - 1);
    k = mdBinarySearch(times, nt, time);
    if (k >= 0) return mdSampleValue(c, values, cubic, k);
    k = ~k - 1;
    if (strcmp(interp, "STEP") == 0) return mdSampleValue(c, values, cubic, k);
    if (k + 1 >= nt || k < 0) mdIndexOut(c);
    f = (time - times[k]) / (times[k + 1] - times[k]);
    a = mdSampleValue(c, values, cubic, k);
    b = mdSampleValue(c, values, cubic, k + 1);
    r = mdVec(c, a.n);
    if (rotation) {
        double d = mdV(c, a, 0) * mdV(c, b, 0) + mdV(c, a, 1) * mdV(c, b, 1) + mdV(c, a, 2) * mdV(c, b, 2) + mdV(c, a, 3) * mdV(c, b, 3), s = d < 0 ? -1 : 1, l = 0;
        for (i = 0; i < a.n; i++) r.v[i] = 0;
        for (i = 0; i < 4; i++) r.v[i] = a.v[i] + (b.v[i] * s - a.v[i]) * f;
        for (i = 0; i < r.n; i++) l += r.v[i] * r.v[i];
        l = sqrt(l);
        if (l > 1e-12) for (i = 0; i < 4; i++) r.v[i] /= l;
        return r;
    }
    for (i = 0; i < a.n; i++) r.v[i] = a.v[i] + (mdV(c, b, i) - a.v[i]) * f;
    return r;
}

/* Decompose: a glTF matrix (column major) into translation, rotation and scale (no shear) */
static void mdDecompose(FcCtx* c, MdVec m, MdPart* p) {
    double c0[3], c1[3], c2[3], sx, sy, sz, r00, r10, r20, r01, r11, r21, r02, r12, r22, tr, qx, qy, qz, qw;
    double t[3], s[3], q[4];
    c0[0] = mdV(c, m, 0); c0[1] = mdV(c, m, 1); c0[2] = mdV(c, m, 2);
    c1[0] = mdV(c, m, 4); c1[1] = mdV(c, m, 5); c1[2] = mdV(c, m, 6);
    c2[0] = mdV(c, m, 8); c2[1] = mdV(c, m, 9); c2[2] = mdV(c, m, 10);
    sx = sqrt(c0[0] * c0[0] + c0[1] * c0[1] + c0[2] * c0[2]);
    sy = sqrt(c1[0] * c1[0] + c1[1] * c1[1] + c1[2] * c1[2]);
    sz = sqrt(c2[0] * c2[0] + c2[1] * c2[1] + c2[2] * c2[2]);
    if (c0[0] * (c1[1] * c2[2] - c1[2] * c2[1]) - c1[0] * (c0[1] * c2[2] - c0[2] * c2[1]) + c2[0] * (c0[1] * c1[2] - c0[2] * c1[1]) < 0) sx = -sx;
    t[0] = mdV(c, m, 12); t[1] = mdV(c, m, 13); t[2] = mdV(c, m, 14);
    p->t = mdVecOf(c, 3, t);
    s[0] = sx; s[1] = sy; s[2] = sz;
    p->s = mdVecOf(c, 3, s);
    r00 = c0[0] / sx; r10 = c0[1] / sx; r20 = c0[2] / sx; r01 = c1[0] / sy; r11 = c1[1] / sy; r21 = c1[2] / sy; r02 = c2[0] / sz; r12 = c2[1] / sz; r22 = c2[2] / sz;
    tr = r00 + r11 + r22;
    if (tr > 0) { double ss = sqrt(tr + 1) * 2; qw = ss / 4; qx = (r21 - r12) / ss; qy = (r02 - r20) / ss; qz = (r10 - r01) / ss; }
    else if (r00 > r11 && r00 > r22) { double ss = sqrt(1 + r00 - r11 - r22) * 2; qw = (r21 - r12) / ss; qx = ss / 4; qy = (r01 + r10) / ss; qz = (r02 + r20) / ss; }
    else if (r11 > r22) { double ss = sqrt(1 + r11 - r00 - r22) * 2; qw = (r02 - r20) / ss; qx = (r01 + r10) / ss; qy = ss / 4; qz = (r12 + r21) / ss; }
    else { double ss = sqrt(1 + r22 - r00 - r11) * 2; qw = (r10 - r01) / ss; qx = (r02 + r20) / ss; qy = (r12 + r21) / ss; qz = ss / 4; }
    q[0] = qx; q[1] = qy; q[2] = qz; q[3] = qw;
    p->r = mdVecOf(c, 4, q);
}

/* ---- Convert ---- */
typedef struct MdBytes { const uint8_t* p; size_t n; } MdBytes;
typedef struct MdGltf {
    FcCtx* c;
    FjValue* root;
    FjValue* accessors;
    FjValue* views;
    FjValue* nodes;
    FjValue* materials;
    FjValue* meshes;
    MdBytes* buffers;
    int bufferCount;
    MdPart** parts;
    int partCount, partCap;
    int* partOfNode;   /* by node: the part, -1: none yet */
    const char* dir;
} MdGltf;

static uint32_t mdU32(const uint8_t* p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }

/* Convert.FromBase64String (white space skipped, padding at the end) */
static MdBytes mdBase64(FcCtx* c, const char* s) {
    static const char* bad = "The input is not a valid Base-64 string as it contains a non-base 64 character, more than two padding characters, or an illegal character among the padding characters.";
    FcBuf out = {0};
    uint32_t acc = 0;
    int bits = 0, count = 0, pad = 0;
    const unsigned char* p;
    MdBytes r;
    fcBufStr(&out, "");
    for (p = (const unsigned char*)s; *p; p++) {
        unsigned char ch = *p;
        int v;
        if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') continue;
        if (ch == '=') { pad++; count++; continue; }
        if (pad > 0) { fcBufFree(&out); mdThrow(c, "System.FormatException", bad); }
        if (ch >= 'A' && ch <= 'Z') v = ch - 'A';
        else if (ch >= 'a' && ch <= 'z') v = ch - 'a' + 26;
        else if (ch >= '0' && ch <= '9') v = ch - '0' + 52;
        else if (ch == '+') v = 62;
        else if (ch == '/') v = 63;
        else { fcBufFree(&out); mdThrow(c, "System.FormatException", bad); }
        acc = (acc << 6) | (uint32_t)v;
        bits += 6;
        count++;
        if (bits >= 8) { bits -= 8; mdW8(&out, (uint8_t)(acc >> bits)); }
    }
    if (count % 4 != 0 || pad > 2) { fcBufFree(&out); mdThrow(c, "System.FormatException", bad); }
    if (pad > 0 && (acc & ((1u << bits) - 1)) != 0) { /* (.NET takes non-zero bits before the padding) */ }
    r.n = out.len;
    r.p = (const uint8_t*)fcBufDone(c, &out);
    return r;
}

/* Uri.UnescapeDataString: %XX of UTF-8; a sequence that is no UTF-8 stays as it is */
static const char* mdUnescape(FcCtx* c, const char* s) {
    FcBuf b = {0};
    size_t i = 0, n = strlen(s);
    fcBufStr(&b, "");
    while (i < n) {
        if (s[i] == '%' && i + 2 < n && fjHex((unsigned char)s[i + 1]) >= 0 && fjHex((unsigned char)s[i + 2]) >= 0) {
            /* a run of escapes, decoded as UTF-8 */
            size_t j = i;
            unsigned char bytes[1024];
            int count = 0, k = 0;
            while (j + 2 < n && s[j] == '%' && fjHex((unsigned char)s[j + 1]) >= 0 && fjHex((unsigned char)s[j + 2]) >= 0 && count < (int)sizeof bytes) {
                bytes[count++] = (unsigned char)(fjHex((unsigned char)s[j + 1]) * 16 + fjHex((unsigned char)s[j + 2]));
                j += 3;
            }
            while (k < count) {
                unsigned char b0 = bytes[k];
                int len = b0 < 0x80 ? 1 : (b0 & 0xE0) == 0xC0 ? 2 : (b0 & 0xF0) == 0xE0 ? 3 : (b0 & 0xF8) == 0xF0 ? 4 : 0, q;
                bool ok = len > 0 && k + len <= count;
                for (q = 1; ok && q < len; q++) if ((bytes[k + q] & 0xC0) != 0x80) ok = false;
                if (ok) {
                    FcU16 u = {0};
                    fcDecodeText(bytes + k, (size_t)len, &u);
                    fcBufStr(&b, fc8FromU16(c, u.p, u.len));
                    fcU16Free(&u);
                    k += len;
                } else { fcBufPut(&b, s + i + (size_t)k * 3, 3); k++; }
            }
            i = j;
        } else fcBufChar(&b, s[i++]);
    }
    return fcBufDone(c, &b);
}
static const char* mdNative(FcCtx* c, const char* fc8) { FcBuf b = {0}; fcBufStr(&b, ""); fcOut8(&b, fc8); return fcBufDone(c, &b); }

/* File.ReadAllBytes with the exceptions of .NET for what is missing */
static MdBytes mdReadAllBytes(FcCtx* c, const char* path) {
    MdBytes r;
    size_t n = 0;
    uint8_t* data;
    const char* full = fcFullPath(c, ".", path);
    struct stat st;
    if (stat(full, &st) == 0 && S_ISDIR(st.st_mode)) mdThrow(c, "System.UnauthorizedAccessException", fcFmt(c, "Access to the path '%s' is denied.", full));
    data = fbReadFile(full, &n);
    if (!data) {
        /* (a folder of the path missing: DirectoryNotFoundException) */
        const char* dir = fcDirectoryName(c, full);
        if (stat(dir, &st) != 0) mdThrow(c, "System.IO.DirectoryNotFoundException", fcFmt(c, "Could not find a part of the path '%s'.", full));
        mdThrow(c, "System.IO.FileNotFoundException", fcFmt(c, "Could not find file '%s'.", full));
    }
    r.p = (const uint8_t*)fcAlloc(c, n + 1);
    if (n) memcpy((void*)r.p, data, n);
    free(data);
    r.n = n;
    return r;
}

/* Read: an accessor as numbers */
static MdVec* mdRead(MdGltf* g, int index, int* countOut) {
    FcCtx* c = g->c;
    FjValue* acc = mdAt(c, g->accessors, index);
    int count = mdInt(c, mdGet(c, acc, "count"));
    int comp = mdInt(c, mdGet(c, acc, "componentType"));
    const char* type = mdStr(c, mdGet(c, acc, "type"));
    FjValue* nz = mdTry(c, acc, "normalized");
    bool normalized = nz && mdBool(c, nz);
    int n, size, i, k, start, stride;
    FjValue *bvEl, *bv, *bo, *ao, *st;
    MdVec* result;
    MdBytes buf;
    int bufIndex;
    if (!type) type = "SCALAR";
    n = strcmp(type, "SCALAR") == 0 ? 1 : strcmp(type, "VEC2") == 0 ? 2 : strcmp(type, "VEC3") == 0 ? 3 : strcmp(type, "VEC4") == 0 ? 4 : strcmp(type, "MAT4") == 0 ? 16 : 0;
    if (n == 0) mdInvalidData(c, fcFmt(c, "Accessor type %s is not read.", type));
    size = comp == 5120 || comp == 5121 ? 1 : comp == 5122 || comp == 5123 ? 2 : comp == 5125 || comp == 5126 ? 4 : 0;
    if (size == 0) mdInvalidData(c, fcFmt(c, "Component type %d is not read.", comp));
    if (count < 0) mdThrow(c, "System.OverflowException", "Arithmetic operation resulted in an overflow.");
    result = (MdVec*)fcAlloc(c, sizeof(MdVec) * (size_t)(count > 0 ? count : 1));
    *countOut = count;
    bvEl = mdTry(c, acc, "bufferView");
    if (!bvEl) { for (i = 0; i < count; i++) { result[i] = mdVec(c, n); memset(result[i].v, 0, sizeof(double) * (size_t)n); } return result; }
    bv = mdAt(c, g->views, mdInt(c, bvEl));
    bufIndex = mdInt(c, mdGet(c, bv, "buffer"));
    if (bufIndex < 0 || bufIndex >= g->bufferCount) mdListIndexOut(c);
    buf = g->buffers[bufIndex];
    bo = mdTry(c, bv, "byteOffset");
    start = bo ? mdInt(c, bo) : 0;
    ao = mdTry(c, acc, "byteOffset");
    start = (int)((uint32_t)start + (uint32_t)(ao ? mdInt(c, ao) : 0));
    st = mdTry(c, bv, "byteStride");
    stride = st ? mdInt(c, st) : n * size;
    for (i = 0; i < count; i++) {
        MdVec v = mdVec(c, n);
        for (k = 0; k < n; k++) {
            int at = (int)((uint32_t)start + (uint32_t)i * (uint32_t)stride + (uint32_t)(k * size));
            int end = (int)((uint32_t)at + (uint32_t)size);
            const uint8_t* b;
            if ((int64_t)end > (int64_t)buf.n) mdInvalidData(c, "An accessor reaches past its buffer.");
            if (at < 0 || (size_t)at + (size_t)size > buf.n) {
                /* (a negative place: buf[at] - IndexOutOfRangeException -, BitConverter - ArgumentOutOfRangeException) */
                if (size == 1) mdIndexOut(c);
                mdThrow(c, "System.ArgumentOutOfRangeException", "Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'startIndex')");
            }
            b = buf.p + at;
            switch (comp) {
                case 5126: { float f; memcpy(&f, b, 4); v.v[k] = f; break; }
                case 5125: v.v[k] = mdU32(b); break;
                case 5123: { uint16_t u = (uint16_t)(b[0] | (b[1] << 8)); v.v[k] = normalized ? u / 65535.0 : u; break; }
                case 5122: { int16_t s = (int16_t)(uint16_t)(b[0] | (b[1] << 8)); v.v[k] = normalized ? mdMax(s / 32767.0, -1) : s; break; }
                case 5121: v.v[k] = normalized ? b[0] / 255.0 : b[0]; break;
                default: v.v[k] = normalized ? mdMax((int8_t)b[0] / 127.0, -1) : (int8_t)b[0]; break;
            }
        }
        result[i] = v;
    }
    return result;
}

static void mdReadMesh(MdGltf* g, FjValue* mesh, MdPart* p) {
    FcCtx* c = g->c;
    FjValue* prims = mdGet(c, mesh, "primitives");
    int pi, np = mdLen(c, prims), i;
    for (pi = 0; pi < np; pi++) {
        FjValue* prim = (FjValue*)prims->items->items[pi];
        FjValue *mode = mdTry(c, prim, "mode"), *attrs, *posEl, *nEl, *uEl, *iEl, *matEl;
        MdVec *pos, *nrm = NULL, *uv = NULL;
        int npos, nn = 0, nu = 0, baseIndex, *idx, nidx;
        if (mode && mdInt(c, mode) != 4) continue;   /* triangles only */
        attrs = mdGet(c, prim, "attributes");
        posEl = mdTry(c, attrs, "POSITION");
        if (!posEl) continue;
        pos = mdRead(g, mdInt(c, posEl), &npos);
        nEl = mdTry(c, attrs, "NORMAL");
        if (nEl) nrm = mdRead(g, mdInt(c, nEl), &nn);
        uEl = mdTry(c, attrs, "TEXCOORD_0");
        if (uEl) uv = mdRead(g, mdInt(c, uEl), &nu);
        baseIndex = p->positions.count;
        iEl = mdTry(c, prim, "indices");
        if (iEl) {
            MdVec* raw = mdRead(g, mdInt(c, iEl), &nidx);
            idx = (int*)fcAlloc(c, sizeof(int) * (size_t)(nidx > 0 ? nidx : 1));
            for (i = 0; i < nidx; i++) idx[i] = mdToInt(raw[i].v[0]);
        } else {
            nidx = npos;
            idx = (int*)fcAlloc(c, sizeof(int) * (size_t)(npos > 0 ? npos : 1));
            for (i = 0; i < npos; i++) idx[i] = i;
        }
        for (i = 0; i < npos; i++) mdVecAdd(c, &p->positions, pos[i]);
        for (i = 0; i < npos; i++) {
            if (nrm) { if (i >= nn) mdIndexOut(c); mdVecAdd(c, &p->normals, nrm[i]); }
            else { MdVec z = mdVec(c, 3); z.v[0] = z.v[1] = z.v[2] = 0; mdVecAdd(c, &p->normals, z); }
            if (uv) { if (i >= nu) mdIndexOut(c); mdVecAdd(c, &p->uvs, uv[i]); }
            else { MdVec z = mdVec(c, 2); z.v[0] = z.v[1] = 0; mdVecAdd(c, &p->uvs, z); }
        }
        if (uv) p->hasUv = true;
        for (i = 0; i < nidx; i++) {
            if (idx[i] < 0 || idx[i] >= npos) mdInvalidData(c, "An index past the vertices.");
            mdIntAdd(c, &p->indices, baseIndex + idx[i]);
        }
        if (!nrm) mdSmoothNormals(c, p, baseIndex, idx, nidx);
        if (!p->color && (matEl = mdTry(c, prim, "material")) != NULL && g->materials && g->materials->kind == FJK_ARRAY) {
            FjValue* mat = mdAt(c, g->materials, mdInt(c, matEl));
            FjValue *pbr, *bc;
            MdVec col;
            static const double white[4] = { 1, 1, 1, 1 };
            col = mdVecOf(c, 4, white);
            if ((pbr = mdTry(c, mat, "pbrMetallicRoughness")) != NULL && (bc = mdTry(c, pbr, "baseColorFactor")) != NULL) col = mdDoubles(c, bc);
            p->color = (uint8_t*)fcAlloc(c, (size_t)(col.n > 0 ? col.n : 1));
            p->colorCount = col.n;
            for (i = 0; i < col.n; i++) p->color[i] = (uint8_t)mdClampI(mdToInt(mdRound(col.v[i] * 255)), 0, 255);
        }
    }
    if (p->positions.count > 65535) mdInvalidData(c, fcFmt(c, "The mesh of '%s' has %d corners (at most 65535 a part).", p->name, p->positions.count));
}

static void mdVisit(MdGltf* g, int node, int parent) {
    FcCtx* c = g->c;
    FjValue *nd, *nm, *mx, *t, *r, *s, *meshEl, *ch;
    MdPart* p;
    int index, i;
    if (node >= 0 && node < mdLen(c, g->nodes) && g->partOfNode[node] >= 0) mdInvalidData(c, "A node is in the tree twice.");
    nd = mdAt(c, g->nodes, node);
    p = mdPartNew(c);
    p->parent = parent;
    nm = mdTry(c, nd, "name");
    if (nm) { const char* sn = mdStr(c, nm); p->name = sn ? sn : ""; }
    else p->name = fcFmt(c, "node%d", node);
    if ((mx = mdTry(c, nd, "matrix")) != NULL) mdDecompose(c, mdDoubles(c, mx), p);
    if ((t = mdTry(c, nd, "translation")) != NULL) p->t = mdDoubles(c, t);
    if ((r = mdTry(c, nd, "rotation")) != NULL) p->r = mdDoubles(c, r);
    if ((s = mdTry(c, nd, "scale")) != NULL) p->s = mdDoubles(c, s);
    if ((meshEl = mdTry(c, nd, "mesh")) != NULL) mdReadMesh(g, mdAt(c, g->meshes, mdInt(c, meshEl)), p);
    index = g->partCount;
    mdPartPush(c, &g->parts, &g->partCount, &g->partCap, p);
    g->partOfNode[node] = index;
    if (g->partCount > 32767) mdInvalidData(c, "Too many parts.");
    if ((ch = mdTry(c, nd, "children")) != NULL) {
        int n = mdLen(c, ch);
        for (i = 0; i < n; i++) mdVisit(g, mdInt(c, (FjValue*)ch->items->items[i]), index);
    }
}

typedef struct MdChannel { int part; const char* path; double* times; int nt; MdVecList values; const char* interp; } MdChannel;

/* Convert: the .f3d of a glTF file (its bytes; external buffers next to `dir`) */
static const uint8_t* mdConvert(FcCtx* c, const uint8_t* file, size_t fileLen, const char* dir, int fps, size_t* size) {
    MdGltf* g = (MdGltf*)fcAlloc(c, sizeof *g);
    FjValue *root, *asset, *ver, *bufs, *scenes, *anims, *sc;
    const char* vs;
    MdBytes glbBin;
    bool hasGlb = false;
    FjError jerr;
    int i, *roots, rootCount = 0, nodeCount;
    MdClip* clips = NULL;
    int clipCount = 0;
    memset(g, 0, sizeof *g);
    g->c = c;
    g->dir = dir;
    glbBin.p = NULL; glbBin.n = 0;
    if (fileLen >= 12 && mdU32(file) == 0x46546C67) {
        /* .glb: a header, then chunks (JSON, BIN) */
        int at = 12;
        const char* json = NULL;
        while ((int64_t)at + 8 <= (int64_t)fileLen) {
            int len = (int)mdU32(file + at);
            uint32_t type = mdU32(file + at + 4);
            int end = (int)((uint32_t)at + 8u + (uint32_t)len);
            if ((int64_t)end > (int64_t)fileLen) mdInvalidData(c, "The .glb file is cut off.");
            if (len < 0) mdThrow(c, "System.ArgumentOutOfRangeException", "Specified argument was out of the range of valid values. (Parameter 'count')");
            if (type == 0x4E4F534A) {
                /* Encoding.UTF8.GetString (a BOM stays a character: a BOM put before it is the one fcDecodeText takes away) */
                FcU16 u = {0};
                uint8_t* withBom = (uint8_t*)fcAlloc(c, (size_t)len + 3);
                withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                if (len) memcpy(withBom + 3, file + at + 8, (size_t)len);
                fcDecodeText(withBom, (size_t)len + 3, &u);
                json = fc8FromU16(c, u.p, u.len);
                fcU16Free(&u);
            } else if (type == 0x004E4942) {
                uint8_t* b = (uint8_t*)fcAlloc(c, (size_t)len + 1);
                if (len) memcpy(b, file + at + 8, (size_t)len);
                glbBin.p = b; glbBin.n = (size_t)len;
                hasGlb = true;
            }
            at = end;
        }
        if (!json) mdInvalidData(c, "The .glb file has no JSON.");
        json = mdNative(c, json);
        root = fjParseStrict(c, (const unsigned char*)json, strlen(json), &jerr);
        if (!root) mdThrow(c, "System.Text.Json.JsonReaderException", jerr.message);   /* (JsonException: not caught there) */
    } else {
        root = fjParseStrict(c, file, fileLen, &jerr);
        if (!root) mdInvalidData(c, fcFmt(c, "Not glTF (%s).", jerr.message));
    }
    g->root = root;
    asset = mdTry(c, root, "asset");
    ver = asset ? mdTry(c, asset, "version") : NULL;
    vs = ver ? mdStr(c, ver) : NULL;
    if (!asset || !ver || !fcStartsWith(vs ? vs : "", "2")) mdInvalidData(c, "Only glTF 2.0 is read.");

    /* buffers */
    if ((bufs = mdTry(c, root, "buffers")) != NULL) {
        int n = mdLen(c, bufs);
        g->buffers = (MdBytes*)fcAlloc(c, sizeof(MdBytes) * (size_t)(n > 0 ? n : 1));
        for (i = 0; i < n; i++) {
            FjValue* b = (FjValue*)bufs->items->items[i];
            FjValue* uriEl = mdTry(c, b, "uri");
            if (uriEl) {
                const char* uri = mdStr(c, uriEl);
                if (!uri) uri = "";
                if (fcStartsWith(uri, "data:")) {
                    const char* comma = strchr(uri, ',');
                    if (!comma || !(comma - uri >= 7 && memcmp(comma - 7, ";base64", 7) == 0)) mdInvalidData(c, "A data URI of a buffer that is not base64.");
                    g->buffers[g->bufferCount++] = mdBase64(c, comma + 1);
                } else {
                    /* readExternal: File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(name))) */
                    const char* name = mdNative(c, mdUnescape(c, uri));
                    g->buffers[g->bufferCount++] = mdReadAllBytes(c, name[0] == '/' ? name : fcFmt(c, "%s/%s", dir, name));
                }
            } else {
                if (!hasGlb) mdInvalidData(c, "A buffer without data.");
                g->buffers[g->bufferCount++] = glbBin;
            }
        }
    }

    g->accessors = fjProp(root, "accessors");
    g->views = fjProp(root, "bufferViews");

    /* the nodes of the scene, parent before child */
    g->nodes = fjProp(root, "nodes");
    if (!g->nodes || g->nodes->kind != FJK_ARRAY || mdLen(c, g->nodes) == 0) mdInvalidData(c, "The glTF file has no nodes.");
    nodeCount = mdLen(c, g->nodes);
    g->partOfNode = (int*)fcAlloc(c, sizeof(int) * (size_t)nodeCount);
    for (i = 0; i < nodeCount; i++) g->partOfNode[i] = -1;
    roots = NULL;
    if ((scenes = mdTry(c, root, "scenes")) != NULL && mdLen(c, scenes) > 0) {
        int si = (sc = mdTry(c, root, "scene")) != NULL ? mdInt(c, sc) : 0;
        FjValue* rn = mdGet(c, mdAt(c, scenes, si), "nodes");
        int n = mdLen(c, rn);
        roots = (int*)fcAlloc(c, sizeof(int) * (size_t)(n > 0 ? n : 1));
        for (i = 0; i < n; i++) roots[rootCount++] = mdInt(c, (FjValue*)rn->items->items[i]);
    } else {
        bool* isChild = (bool*)fcAlloc(c, sizeof(bool) * (size_t)nodeCount);
        memset(isChild, 0, sizeof(bool) * (size_t)nodeCount);
        for (i = 0; i < nodeCount; i++) {
            FjValue* nd = (FjValue*)g->nodes->items->items[i];
            FjValue* ch = mdTry(c, nd, "children");
            if (ch) {
                int k, n = mdLen(c, ch);
                for (k = 0; k < n; k++) { int x = mdInt(c, (FjValue*)ch->items->items[k]); if (x >= 0 && x < nodeCount) isChild[x] = true; }
            }
        }
        roots = (int*)fcAlloc(c, sizeof(int) * (size_t)nodeCount);
        for (i = 0; i < nodeCount; i++) if (!isChild[i]) roots[rootCount++] = i;
    }
    g->materials = fjProp(root, "materials");
    g->meshes = fjProp(root, "meshes");
    for (i = 0; i < rootCount; i++) mdVisit(g, roots[i], -1);

    /* the clips */
    if ((anims = mdTry(c, root, "animations")) != NULL) {
        int na = mdLen(c, anims), ai = 0, a;
        clips = (MdClip*)fcAlloc(c, sizeof(MdClip) * (size_t)(na > 0 ? na : 1));
        for (a = 0; a < na; a++) {
            FjValue* anim = (FjValue*)anims->items->items[a];
            FjValue *an = mdTry(c, anim, "name"), *samplers, *chs;
            const char* name = an ? mdStr(c, an) : "";
            MdChannel* channels;
            int nch, chCount = 0, k, frames, f;
            double duration = 0;
            MdVec** poses;
            if (!name) name = "";
            if (!name[0]) name = fcFmt(c, "animation%d", ai);
            ai++;
            samplers = mdGet(c, anim, "samplers");
            chs = mdGet(c, anim, "channels");
            nch = mdLen(c, chs);
            channels = (MdChannel*)fcAlloc(c, sizeof(MdChannel) * (size_t)(nch > 0 ? nch : 1));
            for (k = 0; k < nch; k++) {
                FjValue* ch = (FjValue*)chs->items->items[k];
                FjValue *target = mdGet(c, ch, "target"), *tn, *smp, *ip;
                const char* path;
                int part, nt, nv, q, tnode;
                MdVec *tv, *vv;
                MdChannel* m;
                tn = mdTry(c, target, "node");
                if (!tn) continue;
                tnode = mdInt(c, tn);
                if (tnode < 0 || tnode >= nodeCount || g->partOfNode[tnode] < 0) continue;
                part = g->partOfNode[tnode];
                path = mdStr(c, mdGet(c, target, "path"));
                if (!path) path = "";
                if (strcmp(path, "translation") != 0 && strcmp(path, "rotation") != 0 && strcmp(path, "scale") != 0) continue;
                smp = mdAt(c, samplers, mdInt(c, mdGet(c, ch, "sampler")));
                tv = mdRead(g, mdInt(c, mdGet(c, smp, "input")), &nt);
                vv = mdRead(g, mdInt(c, mdGet(c, smp, "output")), &nv);
                m = &channels[chCount];
                memset(m, 0, sizeof *m);
                m->times = (double*)fcAlloc(c, sizeof(double) * (size_t)(nt > 0 ? nt : 1));
                for (q = 0; q < nt; q++) m->times[q] = tv[q].v[0];
                m->nt = nt;
                for (q = 0; q < nv; q++) mdVecAdd(c, &m->values, vv[q]);
                ip = mdTry(c, smp, "interpolation");
                m->interp = ip ? mdStr(c, ip) : "LINEAR";
                if (!m->interp) m->interp = "LINEAR";
                if (nt == 0) continue;
                duration = mdMax(duration, m->times[nt - 1]);
                m->part = part;
                m->path = path;
                chCount++;
            }
            /* a clip loops: its frames cover [0, duration) - the last key is the first again */
            frames = mdToInt(mdRound(duration * fps));
            if (frames < 1) frames = 1;
            poses = (MdVec**)fcAlloc(c, sizeof(MdVec*) * (size_t)frames);
            for (f = 0; f < frames; f++) {
                double time = (double)f / fps;
                poses[f] = (MdVec*)fcAlloc(c, sizeof(MdVec) * (size_t)(g->partCount > 0 ? g->partCount : 1));
                for (k = 0; k < g->partCount; k++) {
                    MdPart* pp = g->parts[k];
                    MdVec pose = mdVec(c, pp->t.n + pp->r.n + pp->s.n);
                    memcpy(pose.v, pp->t.v, sizeof(double) * (size_t)pp->t.n);
                    memcpy(pose.v + pp->t.n, pp->r.v, sizeof(double) * (size_t)pp->r.n);
                    memcpy(pose.v + pp->t.n + pp->r.n, pp->s.v, sizeof(double) * (size_t)pp->s.n);
                    poses[f][k] = pose;
                }
                for (k = 0; k < chCount; k++) {
                    MdChannel* m = &channels[k];
                    MdVec v = mdSample(c, m->times, m->nt, &m->values, m->interp, time, strcmp(m->path, "rotation") == 0);
                    int off = strcmp(m->path, "translation") == 0 ? 0 : strcmp(m->path, "rotation") == 0 ? 3 : 7, q;
                    for (q = 0; q < v.n; q++) mdSetV(c, poses[f][m->part], off + q, v.v[q]);
                }
            }
            if (fc8Units(name) > 31) name = mdUnits(c, name, 31);
            clips[clipCount].name = name;
            clips[clipCount].frames = frames;
            clips[clipCount].poses = poses;
            clips[clipCount].loop = true;
            clipCount++;
        }
        if (clipCount > 65535) mdInvalidData(c, "Too many animations.");
    }
    return mdWrite(c, g->parts, g->partCount, clips, clipCount, fps, size);
}

static bool mdExtIs(const char* path, const char* ext) {
    const char* dot = strrchr(path, '.');
    const char* slash = strrchr(path, '/');
    if (!dot || (slash && dot < slash)) return false;
    return fcAsciiIEq(dot, ext);
}
/* IsModelSource */
static bool mdIsModelSource(const char* path) { return mdExtIs(path, ".gltf") || mdExtIs(path, ".glb") || mdExtIs(path, ".fblocks") || mdExtIs(path, ".fmesh"); }

/* ConvertFile: the .f3d of a model file; NULL and the exception (the name of its .NET type, its message) when it cannot be read */
static const uint8_t* mdConvertFile(FcCtx* c, const char* path, int fps, size_t* size, const char** errType, const char** errMessage) {
    const uint8_t* volatile result = NULL;
    *errType = NULL;
    *errMessage = NULL;
    FC_TRY(c) {
        if (mdExtIs(path, ".fblocks") || mdExtIs(path, ".fmesh")) {
            MdBytes b = mdReadAllBytes(c, path);
            FcU16 u = {0};
            const char* text;
            fcDecodeText(b.p, b.n, &u);   /* File.ReadAllText */
            text = fc8FromU16(c, u.p, u.len);
            fcU16Free(&u);
            result = mdExtIs(path, ".fblocks") ? mdConvertBlocks(c, text, size) : mdConvertMesh(c, text, size);
        } else {
            MdBytes data = mdReadAllBytes(c, path);
            result = mdConvert(c, data.p, data.n, fcDirectoryName(c, fcFullPath(c, ".", path)), fps, size);
        }
    } FC_CATCH(c) {
        *errType = c->err.file ? c->err.file : "System.Exception";
        *errMessage = c->err.message ? c->err.message : "";
        result = NULL;
    } FC_END_TRY;
    return result;
}
