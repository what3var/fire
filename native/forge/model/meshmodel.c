/* native/forge/model/meshmodel.c - the mesh models (`.fmesh`, docs/graphics/models.md) as forge turns them into the model file (Phase D 4):
 * src/fire.Compiler/MeshModel.cs (commit 6c1b0f0e), the part a build uses - CreateDefault (AddBox), Parse, Normal (Newell's), ToF3d. The parts and
 * animations are those of the block models (model/blockmodel.c). The editing (extrude, mirror, weld, the text of ToJson, picking) is spark's and stays
 * in C#. */

typedef struct MmFace { int* v; int n; int color, part; } MmFace;
typedef struct MmModel {
    MdVecList vertices;
    MmFace* faces; int faceCount, faceCap;
    uint32_t* palette; int paletteCount;
    BmPart* parts; int partCount;
    BmAnimation* animations; int animationCount;
} MmModel;

static void mmAddFace(FcCtx* c, MmModel* m, const int* v, int n, int color, int part) {
    MmFace* f;
    if (m->faceCount == m->faceCap) {
        int cap = m->faceCap ? m->faceCap * 2 : 32;
        MmFace* faces = (MmFace*)fcAlloc(c, sizeof(MmFace) * (size_t)cap);
        if (m->faceCount) memcpy(faces, m->faces, sizeof(MmFace) * (size_t)m->faceCount);
        m->faces = faces; m->faceCap = cap;
    }
    f = &m->faces[m->faceCount++];
    f->v = (int*)fcAlloc(c, sizeof(int) * (size_t)(n > 0 ? n : 1));
    memcpy(f->v, v, sizeof(int) * (size_t)n);
    f->n = n; f->color = color; f->part = part;
}

/* CreateDefault: a box of a meter on the floor (its bottom in the middle of the origin) of the first bright colour (CreateEmpty, AddBox) */
static MmModel* mmCreateDefault(FcCtx* c) {
    static const int quads[6][4] = { { 1, 3, 7, 5 }, { 0, 4, 6, 2 }, { 2, 6, 7, 3 }, { 0, 1, 5, 4 }, { 4, 5, 7, 6 }, { 0, 2, 3, 1 } };
    static const double mn[3] = { -0.5, 0, -0.5 }, mx[3] = { 0.5, 1, 0.5 };
    MmModel* m = (MmModel*)fcAlloc(c, sizeof *m);
    int i, b, k;
    memset(m, 0, sizeof *m);
    m->palette = (uint32_t*)fcAlloc(c, sizeof bmDefaultPalette);
    memcpy(m->palette, bmDefaultPalette, sizeof bmDefaultPalette);
    m->paletteCount = 16;
    m->parts = (BmPart*)fcAlloc(c, sizeof(BmPart));
    m->parts[0].name = "body"; m->parts[0].parent = -1; m->parts[0].pivot[0] = m->parts[0].pivot[1] = m->parts[0].pivot[2] = 0;
    m->partCount = 1;
    b = m->vertices.count;
    for (i = 0; i < 8; i++) {
        MdVec v = mdVec(c, 3);
        v.v[0] = (i & 1) != 0 ? mx[0] : mn[0]; v.v[1] = (i & 2) != 0 ? mx[1] : mn[1]; v.v[2] = (i & 4) != 0 ? mx[2] : mn[2];
        mdVecAdd(c, &m->vertices, v);
    }
    /* corner i: x = bit 0, y = bit 1, z = bit 2; each face counter clockwise seen from outside */
    for (i = 0; i < 6; i++) {
        int loop[4];
        for (k = 0; k < 4; k++) loop[k] = b + quads[i][k];
        mmAddFace(c, m, loop, 4, 9, 0);
    }
    return m;
}

static MmModel* mmParse(FcCtx* c, const char* text) {
    MmModel* m;
    FjValue *root, *v, *verts, *faces;
    if (bmBlank(text)) return mmCreateDefault(c);
    root = bmParseJson(c, text, "The mesh model is no valid JSON: ");
    if (root->kind != FJK_OBJECT || (v = fjProp(root, "fmesh")) == NULL || mdInt(c, v) != 1) mdInvalidData(c, "Not a mesh model (\"fmesh\": 1 is missing).");
    m = (MmModel*)fcAlloc(c, sizeof *m);
    memset(m, 0, sizeof *m);
    bmParsePalette(c, root, &m->palette, &m->paletteCount);
    m->parts = bmParseParts(c, root, &m->partCount);
    if (m->partCount == 0) {
        m->parts = (BmPart*)fcAlloc(c, sizeof(BmPart));
        m->parts[0].name = "body"; m->parts[0].parent = -1; m->parts[0].pivot[0] = m->parts[0].pivot[1] = m->parts[0].pivot[2] = 0;
        m->partCount = 1;
    }
    m->animations = bmParseAnimations(c, root, &m->animationCount);
    if ((verts = mdTry(c, root, "vertices")) != NULL) {
        int n = mdLen(c, verts), i;
        for (i = 0; i < n; i++) {
            MdVec a = mdDoubles(c, (FjValue*)verts->items->items[i]);
            if (a.n != 3) mdInvalidData(c, "A corner is [x, y, z].");
            mdVecAdd(c, &m->vertices, a);
        }
    }
    if ((faces = mdTry(c, root, "faces")) != NULL) {
        int n = mdLen(c, faces), i;
        for (i = 0; i < n; i++) {
            FjValue* f = (FjValue*)faces->items->items[i];
            int na = mdLen(c, f), k;
            int* a = (int*)fcAlloc(c, sizeof(int) * (size_t)(na > 0 ? na : 1));
            for (k = 0; k < na; k++) a[k] = mdInt(c, (FjValue*)f->items->items[k]);
            if (na < 5) mdInvalidData(c, "A face is [colour, part, v0, v1, v2, ...].");
            for (k = 2; k < na; k++) if (a[k] < 0 || a[k] >= m->vertices.count) mdInvalidData(c, "A face names a corner that is not there.");
            mmAddFace(c, m, a + 2, na - 2, mdClampI(a[0], 0, m->paletteCount - 1), mdClampI(a[1], 0, m->partCount - 1));
        }
    }
    return m;
}

/* Normal: the normal of a face (unit length; Newell's - right for polygons that are not quite flat, zero for a face without area) */
static void mmNormal(const MmModel* m, const MmFace* f, double* n) {
    int i;
    double l;
    n[0] = n[1] = n[2] = 0;
    for (i = 0; i < f->n; i++) {
        const double* a = m->vertices.items[f->v[i]].v;
        const double* b = m->vertices.items[f->v[(i + 1) % f->n]].v;
        n[0] += (a[1] - b[1]) * (a[2] + b[2]);
        n[1] += (a[2] - b[2]) * (a[0] + b[0]);
        n[2] += (a[0] - b[0]) * (a[1] + b[1]);
    }
    /* Normalize: Mul(a, 1 / l) */
    l = sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
    if (l > 1e-12) { double r = 1 / l; n[0] *= r; n[1] *= r; n[2] *= r; }
    else n[0] = n[1] = n[2] = 0;
}

/* ToF3d: a part of the file for each part (at its pivot), under it one for each colour it uses (flat triangles) */
static const uint8_t* mmToF3d(FcCtx* c, const MmModel* m, size_t* size) {
    static const double origin[3] = { 0, 0, 0 };
    int* indexOf = (int*)fcAlloc(c, sizeof(int) * (size_t)(m->partCount > 0 ? m->partCount : 1));
    int* fcolors = (int*)fcAlloc(c, sizeof(int) * (size_t)(m->faceCount > 0 ? m->faceCount : 1));
    MdPart** parts = NULL;
    int partCount = 0, partCap = 0, i, fi;
    for (i = 0; i < m->partCount; i++) {
        const BmPart* p = &m->parts[i];
        const double* parentPivot = p->parent >= 0 ? m->parts[p->parent].pivot : origin;
        MdPart* fp = mdPartNew(c);
        int *colors, nc = 0, ci;
        indexOf[i] = partCount;
        fp->name = fc8Units(p->name) > 31 ? mdUnits(c, p->name, 31) : p->name;
        fp->parent = p->parent >= 0 ? indexOf[p->parent] : -1;
        fp->t.v[0] = p->pivot[0] - parentPivot[0]; fp->t.v[1] = p->pivot[1] - parentPivot[1]; fp->t.v[2] = p->pivot[2] - parentPivot[2];
        mdPartPush(c, &parts, &partCount, &partCap, fp);
        for (fi = 0; fi < m->faceCount; fi++) if (m->faces[fi].part == i) fcolors[nc++] = m->faces[fi].color;
        nc = bmSortedColors(c, fcolors, nc, &colors);
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
            for (fi = 0; fi < m->faceCount; fi++) {
                const MmFace* f = &m->faces[fi];
                double n[3];
                int axis, b, k;
                if (f->part != i || f->color != color) continue;
                mmNormal(m, f, n);
                if (sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]) < 0.5) continue;
                /* texture coordinates on the plane the face is most turned to (a copy of a texture a meter) */
                axis = fabs(n[0]) > fabs(n[1]) ? (fabs(n[0]) > fabs(n[2]) ? 0 : 2) : (fabs(n[1]) > fabs(n[2]) ? 1 : 2);
                b = mesh->positions.count;
                for (k = 0; k < f->n; k++) {
                    const double* cc = m->vertices.items[f->v[k]].v;
                    MdVec pos = mdVec(c, 3), uv = mdVec(c, 2);
                    pos.v[0] = cc[0] - p->pivot[0]; pos.v[1] = cc[1] - p->pivot[1]; pos.v[2] = cc[2] - p->pivot[2];
                    uv.v[0] = cc[(axis + 1) % 3]; uv.v[1] = cc[(axis + 2) % 3];
                    mdVecAdd(c, &mesh->positions, pos);
                    mdVecAdd(c, &mesh->normals, mdVecOf(c, 3, n));
                    mdVecAdd(c, &mesh->uvs, uv);
                }
                for (k = 1; k + 1 < f->n; k++) { mdIntAdd(c, &mesh->indices, b); mdIntAdd(c, &mesh->indices, b + k); mdIntAdd(c, &mesh->indices, b + k + 1); }
                if (mesh->positions.count > 65531) mdInvalidData(c, fcFmt(c, "The part '%s' has too many faces of one colour for one mesh.", p->name));
            }
            mdPartPush(c, &parts, &partCount, &partCap, mesh);
        }
    }
    if (partCount > 32767) mdInvalidData(c, "Too many parts.");
    return mdWrite(c, parts, partCount, bmClips(c, m->parts, m->partCount, indexOf, partCount, m->animations, m->animationCount, 1, origin), m->animationCount, MD_FPS, size);
}

/* ModelConverter.ConvertMesh */
static const uint8_t* mdConvertMesh(FcCtx* c, const char* text, size_t* n) { return mmToF3d(c, mmParse(c, text), n); }
