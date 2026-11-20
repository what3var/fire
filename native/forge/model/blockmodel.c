/* native/forge/model/blockmodel.c - the block models (`.fblocks`, docs/graphics/models.md) as forge turns them into the model file (Phase D 4):
 * src/fire.Compiler/BlockModel.cs (commit 6c1b0f0e), the part a build uses - CreateDefault, Set, Parse (with ParseParts, ParseAnimations, ParseColor,
 * which the mesh models share), FaceVisible, Quads (the greedy rectangles), ToF3d, Rotation, Mul, PoseOf. The editing (regions, copy, fill, the
 * text of ToJson, picking) is spark's and stays in C#. The blocks: a cell of the grid holds the index of its block + 1 (the dictionary of C#; only
 * looked up - the order of its entries does not reach the file). */

typedef struct BmPart { const char* name; int parent; double pivot[3]; } BmPart;
typedef struct BmKey { int part; double time; double rotate[3], move[3], scale[3]; } BmKey;
typedef struct BmAnimation { const char* name; double length; bool loop; BmKey* keys; int keyCount; } BmAnimation;
typedef struct BmModel {
    int sizeX, sizeY, sizeZ;
    double blockSize;
    uint32_t* palette; int paletteCount;
    BmPart* parts; int partCount;
    BmAnimation* animations; int animationCount;
    int* cells;        /* sizeX * sizeY * sizeZ: 0 empty, else the block's index + 1 */
    int* colors;       /* by block */
    int* partsOf;      /* by block */
    int blockCount, blockCap;
} BmModel;

/* 16 colours to start with: black, greys, white, and the bright and the dark of six hues */
static const uint32_t bmDefaultPalette[16] = {
    0x1A1A1A, 0x6E6E6E, 0xBDBDBD, 0xF5F5F5,
    0xC83232, 0xE8873A, 0xF2D13D, 0x5DAE4B,
    0x2E7D4F, 0x3A9AD9, 0x2F4FA8, 0x8A4FB5,
    0xD970A6, 0x8B5A2B, 0x5C3A1E, 0xE8C99A,
};

static void bmGrid(FcCtx* c, BmModel* m) {
    size_t n = (size_t)m->sizeX * (size_t)m->sizeY * (size_t)m->sizeZ;
    m->cells = (int*)fcAlloc(c, sizeof(int) * n);
    memset(m->cells, 0, sizeof(int) * n);
}
static BmModel* bmCreateDefault(FcCtx* c, int sx, int sy, int sz) {
    BmModel* m = (BmModel*)fcAlloc(c, sizeof *m);
    memset(m, 0, sizeof *m);
    m->sizeX = sx; m->sizeY = sy; m->sizeZ = sz;
    m->blockSize = 0.1;
    m->palette = (uint32_t*)fcAlloc(c, sizeof bmDefaultPalette);
    memcpy(m->palette, bmDefaultPalette, sizeof bmDefaultPalette);
    m->paletteCount = 16;
    m->parts = (BmPart*)fcAlloc(c, sizeof(BmPart));
    m->parts[0].name = "body"; m->parts[0].parent = -1;
    m->parts[0].pivot[0] = sx / 2.0; m->parts[0].pivot[1] = 0; m->parts[0].pivot[2] = sz / 2.0;
    m->partCount = 1;
    bmGrid(c, m);
    return m;
}

static bool bmInside(const BmModel* m, int x, int y, int z) { return x >= 0 && y >= 0 && z >= 0 && x < m->sizeX && y < m->sizeY && z < m->sizeZ; }
static int bmCell(const BmModel* m, int x, int y, int z) { return bmInside(m, x, y, z) ? m->cells[((size_t)y * (size_t)m->sizeZ + (size_t)z) * (size_t)m->sizeX + (size_t)x] : 0; }

/* Set: puts a block (or changes one); false outside the grid */
static bool bmSet(FcCtx* c, BmModel* m, int x, int y, int z, int color, int part) {
    int* cell;
    if (!bmInside(m, x, y, z)) return false;
    cell = &m->cells[((size_t)y * (size_t)m->sizeZ + (size_t)z) * (size_t)m->sizeX + (size_t)x];
    if (*cell == 0) {
        if (m->blockCount == m->blockCap) {
            int cap = m->blockCap ? m->blockCap * 2 : 64;
            int* colors = (int*)fcAlloc(c, sizeof(int) * (size_t)cap);
            int* partsOf = (int*)fcAlloc(c, sizeof(int) * (size_t)cap);
            if (m->blockCount) { memcpy(colors, m->colors, sizeof(int) * (size_t)m->blockCount); memcpy(partsOf, m->partsOf, sizeof(int) * (size_t)m->blockCount); }
            m->colors = colors; m->partsOf = partsOf; m->blockCap = cap;
        }
        *cell = ++m->blockCount;
    }
    m->colors[*cell - 1] = mdClampI(color, 0, m->paletteCount - 1 > 0 ? m->paletteCount - 1 : 0);
    m->partsOf[*cell - 1] = mdClampI(part, 0, m->partCount - 1 > 0 ? m->partCount - 1 : 0);
    return true;
}

/* ParseColor: "#RRGGBB" (uint.TryParse with NumberStyles.HexNumber: white space around it allowed), else white */
static uint32_t bmParseColor(const char* s) {
    FcU16 u = {0};
    int a = 0, b, i;
    uint32_t v = 0;
    bool ok = true;
    fcU16FromFc8(s, &u);
    b = u.len;
    while (a < b && fcIsWhiteSpace(u.p[a])) a++;
    while (b > a && fcIsWhiteSpace(u.p[b - 1])) b--;
    while (a < b && u.p[a] == '#') a++;
    if (b - a != 6) ok = false;
    else {
        int x = a, y = b;
        while (x < y && (u.p[x] == ' ' || (u.p[x] >= 0x09 && u.p[x] <= 0x0D))) x++;
        while (y > x && (u.p[y - 1] == ' ' || (u.p[y - 1] >= 0x09 && u.p[y - 1] <= 0x0D))) y--;
        if (x == y) ok = false;
        for (i = x; ok && i < y; i++) {
            uint16_t ch = u.p[i];
            int d = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1;
            if (d < 0) ok = false; else v = v * 16 + (uint32_t)d;
        }
    }
    fcU16Free(&u);
    return ok ? v : 0xFFFFFF;
}

/* x.EnumerateArray().Select(e => e.GetDouble()).Concat(def x 3).Take(3): only the first three are read */
static void bmThree(FcCtx* c, const FjValue* x, double def, double* out) {
    int n = mdLen(c, x), i;
    for (i = 0; i < 3; i++) out[i] = i < n ? mdDouble(c, (const FjValue*)x->items->items[i]) : def;
}

/* ParseParts: the parts of a model file ("parts": name, parent - an earlier part or -1 -, pivot) */
static BmPart* bmParseParts(FcCtx* c, const FjValue* root, int* count) {
    FjValue* parts = mdTry(c, root, "parts");
    BmPart* list;
    int n, i;
    *count = 0;
    if (!parts) return NULL;
    n = mdLen(c, parts);
    list = (BmPart*)fcAlloc(c, sizeof(BmPart) * (size_t)(n > 0 ? n : 1));
    for (i = 0; i < n; i++) {
        FjValue* p = (FjValue*)parts->items->items[i];
        FjValue *nm = mdTry(c, p, "name"), *pa, *pv;
        BmPart* part = &list[*count];
        const char* s = nm ? mdStr(c, nm) : "part";
        part->name = s ? s : "part";
        pa = mdTry(c, p, "parent");
        part->parent = pa ? mdInt(c, pa) : -1;
        pv = mdTry(c, p, "pivot");
        if (pv) bmThree(c, pv, 0, part->pivot); else part->pivot[0] = part->pivot[1] = part->pivot[2] = 0;
        if (part->parent >= *count) mdInvalidData(c, fcFmt(c, "The part '%s' names a parent that comes after it.", part->name));
        (*count)++;
    }
    return list;
}

/* ParseAnimations: the animations of a model file ("animations": name, length, loop, keys of the parts) */
static BmAnimation* bmParseAnimations(FcCtx* c, const FjValue* root, int* count) {
    FjValue* anims = mdTry(c, root, "animations");
    BmAnimation* list;
    int n, i;
    *count = 0;
    if (!anims) return NULL;
    n = mdLen(c, anims);
    list = (BmAnimation*)fcAlloc(c, sizeof(BmAnimation) * (size_t)(n > 0 ? n : 1));
    for (i = 0; i < n; i++) {
        FjValue* a = (FjValue*)anims->items->items[i];
        FjValue *an = mdTry(c, a, "name"), *al, *lp, *keys;
        BmAnimation* anim = &list[i];
        const char* s = an ? mdStr(c, an) : "animation";
        anim->name = s ? s : "animation";
        al = mdTry(c, a, "length");
        anim->length = al && mdDouble(c, al) > 0 ? mdDouble(c, al) : 1;
        lp = mdTry(c, a, "loop");
        anim->loop = !lp || mdBool(c, lp);
        anim->keys = NULL;
        anim->keyCount = 0;
        if ((keys = mdTry(c, a, "keys")) != NULL) {
            int nk = mdLen(c, keys), k;
            anim->keys = (BmKey*)fcAlloc(c, sizeof(BmKey) * (size_t)(nk > 0 ? nk : 1));
            for (k = 0; k < nk; k++) {
                FjValue* kv = (FjValue*)keys->items->items[k];
                BmKey* key = &anim->keys[k];
                FjValue *kp = mdTry(c, kv, "part"), *kt, *x;
                key->part = kp ? mdInt(c, kp) : 0;
                kt = mdTry(c, kv, "time");
                key->time = kt ? mdMax(0, mdDouble(c, kt)) : 0;
                if ((x = mdTry(c, kv, "rotate")) != NULL) bmThree(c, x, 0, key->rotate); else key->rotate[0] = key->rotate[1] = key->rotate[2] = 0;
                if ((x = mdTry(c, kv, "move")) != NULL) bmThree(c, x, 0, key->move); else key->move[0] = key->move[1] = key->move[2] = 0;
                if ((x = mdTry(c, kv, "scale")) != NULL) bmThree(c, x, 1, key->scale); else key->scale[0] = key->scale[1] = key->scale[2] = 1;
                anim->keyCount++;
            }
        }
        (*count)++;
    }
    return list;
}

/* the palette of a model file ("palette": the colours; none: the default palette) */
static void bmParsePalette(FcCtx* c, const FjValue* root, uint32_t** palette, int* count) {
    FjValue* pal = mdTry(c, root, "palette");
    *palette = NULL;
    *count = 0;
    if (pal) {
        int n = mdLen(c, pal), i;
        *palette = (uint32_t*)fcAlloc(c, sizeof(uint32_t) * (size_t)(n > 0 ? n : 1));
        for (i = 0; i < n; i++) {
            const char* s = mdStr(c, (FjValue*)pal->items->items[i]);
            (*palette)[(*count)++] = bmParseColor(s ? s : "#FFFFFF");
        }
    }
    if (*count == 0) {
        *palette = (uint32_t*)fcAlloc(c, sizeof bmDefaultPalette);
        memcpy(*palette, bmDefaultPalette, sizeof bmDefaultPalette);
        *count = 16;
    }
}

/* string.IsNullOrWhiteSpace */
static bool bmBlank(const char* text) {
    const unsigned char* p = (const unsigned char*)text;
    while (*p) if (!fcIsWhiteSpace(fc8Next(&p))) return false;
    return true;
}

/* JsonDocument.Parse with AllowTrailingCommas and comments skipped: the root, or the InvalidDataException with `what` before the reader's message */
static FjValue* bmParseJson(FcCtx* c, const char* text, const char* what) {
    FjError e;
    FjValue* root = fjParse(c, text, &e, NULL);
    if (!root) mdInvalidData(c, fcFmt(c, "%s%s", what, e.message));
    return root;
}

static BmModel* bmParse(FcCtx* c, const char* text) {
    BmModel* m;
    FjValue *root, *v, *size, *bs, *blocks;
    if (bmBlank(text)) return bmCreateDefault(c, 16, 16, 16);
    m = (BmModel*)fcAlloc(c, sizeof *m);
    memset(m, 0, sizeof *m);
    m->sizeX = m->sizeY = m->sizeZ = 16;
    m->blockSize = 0.1;
    root = bmParseJson(c, text, "The block model is no valid JSON: ");
    if (root->kind != FJK_OBJECT || (v = fjProp(root, "fblocks")) == NULL || mdInt(c, v) != 1) mdInvalidData(c, "Not a block model (\"fblocks\": 1 is missing).");
    if ((size = mdTry(c, root, "size")) != NULL) {
        int n = mdLen(c, size), i, s[3] = { 0, 0, 0 };
        for (i = 0; i < n; i++) { int x = mdInt(c, (FjValue*)size->items->items[i]); if (i < 3) s[i] = x; }
        if (n != 3) mdInvalidData(c, "\"size\" has three numbers.");
        m->sizeX = mdClampI(s[0], 1, 256); m->sizeY = mdClampI(s[1], 1, 256); m->sizeZ = mdClampI(s[2], 1, 256);
    }
    if ((bs = mdTry(c, root, "block")) != NULL) m->blockSize = mdDouble(c, bs) > 0 ? mdDouble(c, bs) : 0.1;
    bmParsePalette(c, root, &m->palette, &m->paletteCount);
    m->parts = bmParseParts(c, root, &m->partCount);
    if (m->partCount == 0) {
        m->parts = (BmPart*)fcAlloc(c, sizeof(BmPart));
        m->parts[0].name = "body"; m->parts[0].parent = -1;
        m->parts[0].pivot[0] = m->sizeX / 2.0; m->parts[0].pivot[1] = 0; m->parts[0].pivot[2] = m->sizeZ / 2.0;
        m->partCount = 1;
    }
    m->animations = bmParseAnimations(c, root, &m->animationCount);
    bmGrid(c, m);
    if ((blocks = mdTry(c, root, "blocks")) != NULL) {
        int n = mdLen(c, blocks), i;
        for (i = 0; i < n; i++) {
            FjValue* b = (FjValue*)blocks->items->items[i];
            int k, na = mdLen(c, b), a[5] = { 0, 0, 0, 0, 0 };
            for (k = 0; k < na; k++) { int x = mdInt(c, (FjValue*)b->items->items[k]); if (k < 5) a[k] = x; }
            if (na < 4) mdInvalidData(c, "A block is [x, y, z, colour] or [x, y, z, colour, part].");
            bmSet(c, m, a[0], a[1], a[2], a[3], na > 4 ? a[4] : 0);
        }
    }
    return m;
}

/* the six directions: +x, -x, +y, -y, +z, -z */
static const int bmDirections[6][3] = { { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 } };

/* FaceVisible: not when a block of the same part is there */
static bool bmFaceVisible(const BmModel* m, int x, int y, int z, int dir, int part) {
    int n = bmCell(m, x + bmDirections[dir][0], y + bmDirections[dir][1], z + bmDirections[dir][2]);
    return n == 0 || m->partsOf[n - 1] != part;
}

typedef struct BmQuad { int dir; double a[3], b[3], cc[3], d[3]; int color, part; } BmQuad;
typedef struct BmQuads { BmQuad* items; int count, cap; } BmQuads;

/* Quads: the visible faces merged greedily - per part, colour, direction and layer the rectangles that cover them */
static BmQuads bmQuadsOf(FcCtx* c, const BmModel* m) {
    BmQuads quads = { NULL, 0, 0 };
    int size[3], dir;
    size[0] = m->sizeX; size[1] = m->sizeY; size[2] = m->sizeZ;
    for (dir = 0; dir < 6; dir++) {
        int axis = dir / 2, sign = dir % 2 == 0 ? 1 : -1, layer;
        int ua = (axis + 1) % 3, va = (axis + 2) % 3;   /* the two axes of the plane */
        int nu = size[ua], nv = size[va];
        int* maskC = (int*)malloc(sizeof(int) * (size_t)nu * (size_t)nv);
        int* maskP = (int*)malloc(sizeof(int) * (size_t)nu * (size_t)nv);
        for (layer = 0; layer < size[axis]; layer++) {
            /* the mask: per cell of the plane the (colour, part) of a visible face, or -1 */
            bool any = false;
            int u, v;
            for (u = 0; u < nu; u++)
                for (v = 0; v < nv; v++) {
                    int cell[3], b;
                    cell[axis] = layer; cell[ua] = u; cell[va] = v;
                    maskC[u * nv + v] = -1; maskP[u * nv + v] = -1;
                    b = bmCell(m, cell[0], cell[1], cell[2]);
                    if (b != 0 && bmFaceVisible(m, cell[0], cell[1], cell[2], dir, m->partsOf[b - 1])) { maskC[u * nv + v] = m->colors[b - 1]; maskP[u * nv + v] = m->partsOf[b - 1]; any = true; }
                }
            if (!any) continue;
            for (v = 0; v < nv; v++)
                for (u = 0; u < nu;) {
                    int mc = maskC[u * nv + v], mp = maskP[u * nv + v], w, h, dv, k;
                    bool ok;
                    double plane;
                    BmQuad* q;
                    if (mc < 0) { u++; continue; }
                    w = 1;
                    while (u + w < nu && maskC[(u + w) * nv + v] == mc && maskP[(u + w) * nv + v] == mp) w++;
                    h = 1;
                    for (ok = true; ok && v + h < nv;) {
                        for (k = 0; k < w; k++) if (maskC[(u + k) * nv + v + h] != mc || maskP[(u + k) * nv + v + h] != mp) { ok = false; break; }
                        if (ok) h++;
                    }
                    for (dv = 0; dv < h; dv++) for (k = 0; k < w; k++) { maskC[(u + k) * nv + v + dv] = -1; maskP[(u + k) * nv + v + dv] = -1; }
                    plane = layer + (sign > 0 ? 1 : 0);
                    if (quads.count == quads.cap) {
                        int cap = quads.cap ? quads.cap * 2 : 64;
                        BmQuad* items = (BmQuad*)fcAlloc(c, sizeof(BmQuad) * (size_t)cap);
                        if (quads.count) memcpy(items, quads.items, sizeof(BmQuad) * (size_t)quads.count);
                        quads.items = items; quads.cap = cap;
                    }
                    q = &quads.items[quads.count++];
                    q->dir = dir; q->color = mc; q->part = mp;
#define BM_P(dst, pu, pv) do { (dst)[axis] = plane; (dst)[ua] = (pu); (dst)[va] = (pv); } while (0)
                    /* (u, v, axis) is right handed: u x v = +axis; counter clockwise seen from +axis is (u0,v0) (u1,v0) (u1,v1) (u0,v1) */
                    if (sign > 0) { BM_P(q->a, u, v); BM_P(q->b, u + w, v); BM_P(q->cc, u + w, v + h); BM_P(q->d, u, v + h); }
                    else { BM_P(q->a, u, v); BM_P(q->b, u, v + h); BM_P(q->cc, u + w, v + h); BM_P(q->d, u + w, v); }
#undef BM_P
                    u += w;
                }
        }
        free(maskC);
        free(maskP);
    }
    return quads;
}

/* Rotation: the rotation (row major 4x4) of angles in degrees around x, y, z: Ry * Rx * Rz (as scene3d turns objects) */
#define MD_PI 3.14159265358979323846   /* Math.PI */
static void bmRotation(const double* deg, double* out) {
    double ax = deg[0] * MD_PI / 180, ay = deg[1] * MD_PI / 180, az = deg[2] * MD_PI / 180;
    double cx = cos(ax), sx = sin(ax), cy = cos(ay), sy = sin(ay), cz = cos(az), sz = sin(az);
    double rx[16] = { 1, 0, 0, 0, 0, cx, -sx, 0, 0, sx, cx, 0, 0, 0, 0, 1 };
    double ry[16] = { cy, 0, sy, 0, 0, 1, 0, 0, -sy, 0, cy, 0, 0, 0, 0, 1 };
    double rz[16] = { cz, -sz, 0, 0, sz, cz, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    double t[16];
    mdMul(rx, rz, t);   /* (BlockModel.Mul: the same sums as ModelConverter.Mul) */
    mdMul(ry, t, out);
}

/* PoseOf: the pose of a part at a time of an animation (shared with the mesh models) - rotation (degrees), movement, scale; between its keys linearly */
static void bmPoseOf(FcCtx* c, const BmAnimation* anim, int part, double time, double* rot, double* move, double* scale) {
    const BmKey** keys;
    int n = 0, i, j;
    double len, t;
    const BmKey *ka, *kb;
    double ta, tb, f;
    rot[0] = rot[1] = rot[2] = 0; move[0] = move[1] = move[2] = 0; scale[0] = scale[1] = scale[2] = 1;
    if (!anim) return;
    keys = (const BmKey**)fcAlloc(c, sizeof(BmKey*) * (size_t)(anim->keyCount > 0 ? anim->keyCount : 1));
    for (i = 0; i < anim->keyCount; i++) if (anim->keys[i].part == part) keys[n++] = &anim->keys[i];
    /* OrderBy(k => k.Time): stable */
    for (i = 1; i < n; i++) { const BmKey* k = keys[i]; for (j = i; j > 0 && keys[j - 1]->time > k->time; j--) keys[j] = keys[j - 1]; keys[j] = k; }
    if (n == 0) return;
    len = anim->length > 0 ? anim->length : 1;
    t = anim->loop ? time - floor(time / len) * len : mdClamp(time, 0, len);
#define BM_OF(k) do { memcpy(rot, (k)->rotate, sizeof(double) * 3); memcpy(move, (k)->move, sizeof(double) * 3); memcpy(scale, (k)->scale, sizeof(double) * 3); return; } while (0)
    if (n == 1) BM_OF(keys[0]);
    if (t < keys[0]->time) {
        if (!anim->loop) BM_OF(keys[0]);
        ka = keys[n - 1]; kb = keys[0]; ta = keys[n - 1]->time - len; tb = keys[0]->time;
    } else {
        ka = NULL;
        for (i = 0; i + 1 < n; i++) if (t < keys[i + 1]->time) { ka = keys[i]; kb = keys[i + 1]; ta = keys[i]->time; tb = keys[i + 1]->time; break; }
        if (!ka) {
            if (!anim->loop) BM_OF(keys[n - 1]);
            ka = keys[n - 1]; kb = keys[0]; ta = keys[n - 1]->time; tb = keys[0]->time + len;
        }
    }
#undef BM_OF
    f = tb > ta ? (t - ta) / (tb - ta) : 0;
    for (i = 0; i < 3; i++) {
        rot[i] = ka->rotate[i] + (kb->rotate[i] - ka->rotate[i]) * f;
        move[i] = ka->move[i] + (kb->move[i] - ka->move[i]) * f;
        scale[i] = ka->scale[i] + (kb->scale[i] - ka->scale[i]) * f;
    }
}

/* the frames of the clips of block and mesh models: every frame the place of every part of the file (the parts of the colours stay where their part is);
 * `s` the size of a unit (a block; 1 for meters), `origin` the pivot of a part without parent */
static MdClip* bmClips(FcCtx* c, const BmPart* parts, int partCount, const int* indexOf, int filePartCount, const BmAnimation* anims, int animCount, double s, const double* origin) {
    MdClip* clips = (MdClip*)fcAlloc(c, sizeof(MdClip) * (size_t)(animCount > 0 ? animCount : 1));
    int fps = MD_FPS, a, f, k, i;
    for (a = 0; a < animCount; a++) {
        const BmAnimation* anim = &anims[a];
        int frames = (int)((uint32_t)mdToInt(mdRound(anim->length * fps)) + (anim->loop ? 0u : 1u));   /* (int arithmetic of C#: wraps) */
        MdVec** poses;
        if (frames < 1) frames = 1;
        poses = (MdVec**)fcAlloc(c, sizeof(MdVec*) * (size_t)frames);
        for (f = 0; f < frames; f++) {
            poses[f] = (MdVec*)fcAlloc(c, sizeof(MdVec) * (size_t)(filePartCount > 0 ? filePartCount : 1));
            for (k = 0; k < filePartCount; k++) {
                static const double rest[10] = { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 };
                poses[f][k] = mdVecOf(c, 10, rest);
            }
            for (i = 0; i < partCount; i++) {
                double rot[3], move[3], scale[3], m[16], q[4], v[10];
                const BmPart* p = &parts[i];
                const double* parentPivot = p->parent >= 0 ? parts[p->parent].pivot : origin;
                bmPoseOf(c, anim, i, (double)f / fps, rot, move, scale);
                bmRotation(rot, m);
                mdQuatOf(m, q);
                v[0] = (p->pivot[0] - parentPivot[0] + move[0]) * s; v[1] = (p->pivot[1] - parentPivot[1] + move[1]) * s; v[2] = (p->pivot[2] - parentPivot[2] + move[2]) * s;
                v[3] = q[0]; v[4] = q[1]; v[5] = q[2]; v[6] = q[3]; v[7] = scale[0]; v[8] = scale[1]; v[9] = scale[2];
                poses[f][indexOf[i]] = mdVecOf(c, 10, v);
            }
        }
        clips[a].name = fc8Units(anim->name) > 31 ? mdUnits(c, anim->name, 31) : anim->name;
        clips[a].frames = frames;
        clips[a].poses = poses;
        clips[a].loop = anim->loop;
    }
    return clips;
}

/* the colours a part uses, ascending (Select(Color).Distinct().OrderBy(c => c)) */
static int bmSortedColors(FcCtx* c, const int* colors, int n, int** out) {
    int* list = (int*)fcAlloc(c, sizeof(int) * (size_t)(n > 0 ? n : 1));
    int count = 0, i, j;
    for (i = 0; i < n; i++) {
        for (j = 0; j < count && list[j] != colors[i]; j++) {}
        if (j == count) list[count++] = colors[i];
    }
    for (i = 1; i < count; i++) { int x = list[i]; for (j = i; j > 0 && list[j - 1] > x; j--) list[j] = list[j - 1]; list[j] = x; }
    *out = list;
    return count;
}

/* ToF3d: a part of the model file for each part (at its pivot), under it one part for each colour it uses (the triangles) */
static const uint8_t* bmToF3d(FcCtx* c, const BmModel* m, size_t* size) {
    BmQuads quads = bmQuadsOf(c, m);
    double s = m->blockSize, ox = m->sizeX / 2.0, oz = m->sizeZ / 2.0, longest = mdMax(m->sizeX, mdMax(m->sizeY, m->sizeZ));
    double origin[3];
    int* indexOf = (int*)fcAlloc(c, sizeof(int) * (size_t)(m->partCount > 0 ? m->partCount : 1));
    MdPart** parts = NULL;
    int partCount = 0, partCap = 0, i, qi;
    int* qcolors = (int*)fcAlloc(c, sizeof(int) * (size_t)(quads.count > 0 ? quads.count : 1));
    origin[0] = ox; origin[1] = 0; origin[2] = oz;
    for (i = 0; i < m->partCount; i++) {
        const BmPart* p = &m->parts[i];
        const double* parentPivot = p->parent >= 0 ? m->parts[p->parent].pivot : origin;
        MdPart* fp = mdPartNew(c);
        int* colors;
        int nc = 0, ci;
        fp->name = fc8Units(p->name) > 31 ? mdUnits(c, p->name, 31) : p->name;
        fp->parent = p->parent >= 0 ? indexOf[p->parent] : -1;
        fp->t.v[0] = (p->pivot[0] - parentPivot[0]) * s; fp->t.v[1] = (p->pivot[1] - parentPivot[1]) * s; fp->t.v[2] = (p->pivot[2] - parentPivot[2]) * s;
        indexOf[i] = partCount;
        mdPartPush(c, &parts, &partCount, &partCap, fp);
        for (qi = 0; qi < quads.count; qi++) if (quads.items[qi].part == i) qcolors[nc++] = quads.items[qi].color;
        nc = bmSortedColors(c, qcolors, nc, &colors);
        for (ci = 0; ci < nc; ci++) {
            int color = colors[ci];
            MdPart* mesh = mdPartNew(c);
            uint32_t rgb = m->palette[mdClampI(color, 0, m->paletteCount - 1)];
            mesh->name = fcFmt(c, "%s#%d", fc8Units(p->name) > 24 ? mdUnits(c, p->name, 24) : p->name, color);
            mesh->parent = indexOf[i];
            mesh->hasUv = true;
            mesh->color = (uint8_t*)fcAlloc(c, 4);
            mesh->color[0] = (uint8_t)(rgb >> 16); mesh->color[1] = (uint8_t)(rgb >> 8); mesh->color[2] = (uint8_t)rgb; mesh->color[3] = 255;
            mesh->colorCount = 4;
            for (qi = 0; qi < quads.count; qi++) {
                const BmQuad* q = &quads.items[qi];
                const double* corners[4];
                int b = mesh->positions.count, k;
                if (q->part != i || q->color != color) continue;
                corners[0] = q->a; corners[1] = q->b; corners[2] = q->cc; corners[3] = q->d;
                for (k = 0; k < 4; k++) {
                    const double* cc = corners[k];
                    int axis = q->dir / 2;
                    MdVec pos = mdVec(c, 3), nrm = mdVec(c, 3), uv = mdVec(c, 2);
                    pos.v[0] = (cc[0] - p->pivot[0]) * s; pos.v[1] = (cc[1] - p->pivot[1]) * s; pos.v[2] = (cc[2] - p->pivot[2]) * s;
                    nrm.v[0] = bmDirections[q->dir][0]; nrm.v[1] = bmDirections[q->dir][1]; nrm.v[2] = bmDirections[q->dir][2];
                    /* texture coordinates on the plane: one copy of a texture over the longest side of the grid */
                    uv.v[0] = cc[(axis + 1) % 3] / longest; uv.v[1] = cc[(axis + 2) % 3] / longest;
                    mdVecAdd(c, &mesh->positions, pos);
                    mdVecAdd(c, &mesh->normals, nrm);
                    mdVecAdd(c, &mesh->uvs, uv);
                }
                mdIntAdd(c, &mesh->indices, b); mdIntAdd(c, &mesh->indices, b + 1); mdIntAdd(c, &mesh->indices, b + 2);
                mdIntAdd(c, &mesh->indices, b); mdIntAdd(c, &mesh->indices, b + 2); mdIntAdd(c, &mesh->indices, b + 3);
                if (mesh->positions.count > 65531) mdInvalidData(c, fcFmt(c, "The part '%s' has too many faces of one colour for one mesh.", p->name));
            }
            mdPartPush(c, &parts, &partCount, &partCap, mesh);
        }
    }
    if (partCount > 32767) mdInvalidData(c, "Too many parts.");
    return mdWrite(c, parts, partCount, bmClips(c, m->parts, m->partCount, indexOf, partCount, m->animations, m->animationCount, s, origin), m->animationCount, MD_FPS, size);
}

/* ModelConverter.ConvertBlocks */
static const uint8_t* mdConvertBlocks(FcCtx* c, const char* text, size_t* n) { return bmToF3d(c, bmParse(c, text), n); }
