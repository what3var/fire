/* fire native bridge "scene3d" (C): the natives behind `#import "scene3d"` (docs/graphics/3d.md) - meshes, textures and the renderer that draws them into RGBA pixels. The fire side (Scene,
 * Camera, Object, Light, Texture, Material, Template, Model - fire source) is the same in the VM and in a native build; this file is what its `__S3D...` functions do.
 *
 * This file is the one implementation, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine runs it in a shared library built from it
 * (native/abi/fire_pkg_abi.h). The renderer itself is s3d_engine.h (plain C; the software renderer) with its hardware backend s3d_gpu_vk.h (Vulkan, loaded when it is first asked for).
 * Meshes, textures and the scene itself (objects, lights, cameras, materials, scenes: "items") live here and are used by handle - the fire classes are thin, their properties read
 * and write the items, and `Render` draws a scene in one call. The pixels are a byte buffer of the fire program (the camera copies it into its framebuffer with `WriteBytes`). */
#ifndef FIRE_BRIDGE_SCENE3D_H
#define FIRE_BRIDGE_SCENE3D_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/* (a package puts its files into one source, the engine first: then it is there already) */
#ifndef FIRE_S3D_ENGINE_H
#include "s3d_engine.h"
#endif
#ifndef FIRE_S3D_LEAN_H
#include "s3d_lean.h"
#endif

/* ---- the tables: slot 0 is never used (handle 0 is "none"), a free slot is used again ---- */
typedef struct FireS3dTable { void** items; s3d_size count, cap; } FireS3dTable;
static FireS3dTable g_s3dMeshes, g_s3dTextures;

static void* fire_s3d_find(FireS3dTable* t, Value h) {
    int64_t i = h.i;
    if (i <= 0 || (s3d_size)i >= t->count) return NULL;
    return t->items[i];
}

/* The handle of `item` (0 when the memory is gone: the caller frees the item then). */
static int64_t fire_s3d_add(FireS3dTable* t, void* item) {
    s3d_size i;
    if (item == NULL) return 0;
    for (i = 1; i < t->count; i++)
        if (t->items[i] == NULL) { t->items[i] = item; return (int64_t)i; }
    if (t->count == 0) {
        if (!S3D_GROW(t->items, t->cap, 1)) return 0;
        t->items[0] = NULL;
        t->count = 1;
    }
    if (!S3D_GROW(t->items, t->cap, t->count + 1)) return 0;
    t->items[t->count] = item;
    return (int64_t)t->count++;
}

static float fire_s3d_F(Value v) { return (float)toR(v); }

static Value fire_s3d_meshHandle(S3dMesh* m) {
    int64_t h = fire_s3d_add(&g_s3dMeshes, m);
    if (h == 0) s3d_meshFree(m);
    return Int(h);
}

/* Mesh from three arrays: positions (x, y, z per vertex), texture coordinates (u, v per vertex; may be empty) and indices (three per triangle). Returns the handle, 0 on bad data. */
static Value fire_s3d_MeshNew(Value positions, Value uvs, Value indices) {
    Arr *p, *t, *ix;
    uint32_t vertices, i;
    S3dMesh* mesh;
    if (positions.kind != K_Array || uvs.kind != K_Array || indices.kind != K_Array) return Int(0);
    p = arrOf(positions); t = arrOf(uvs); ix = arrOf(indices);
    if (p->length == 0 || p->length % 3 != 0 || ix->length == 0 || ix->length % 3 != 0) return Int(0);
    vertices = p->length / 3;
    if (t->length != 0 && t->length != vertices * 2) return Int(0);
    mesh = s3d_meshNew();
    if (mesh == NULL) return Int(0);
    if (!S3D_GROW(mesh->pos, mesh->posCap, p->length) || !S3D_GROW(mesh->uv, mesh->uvCap, (s3d_size)vertices * 2) || !S3D_GROW(mesh->idx, mesh->idxCap, ix->length)) { s3d_meshFree(mesh); return Int(0); }
    for (i = 0; i < p->length; i++) mesh->pos[i] = fire_s3d_F(arrItems(p)[i]);
    mesh->posN = p->length;
    for (i = 0; i < vertices * 2; i++) mesh->uv[i] = i < t->length ? fire_s3d_F(arrItems(t)[i]) : 0.0f;
    mesh->uvN = (s3d_size)vertices * 2;
    for (i = 0; i < ix->length; i++) {
        int64_t k = arrItems(ix)[i].i;
        if (k < 0 || (uint64_t)k >= vertices) { s3d_meshFree(mesh); return Int(0); }
        mesh->idx[i] = (uint32_t)k;
    }
    mesh->idxN = ix->length;
    if (!s3d_meshFinish(mesh)) { s3d_meshFree(mesh); return Int(0); }
    return fire_s3d_meshHandle(mesh);
}

/* A template mesh: 0 box, 1 sphere, 2 cylinder, 3 cone, 4 plane, 5 quad; `detail` is the number of segments around. */
static Value fire_s3d_MeshTemplate(Value kind, Value detail) {
    S3dMesh* m;
    if (kind.i < 0 || kind.i > 5) return Int(0);
    m = s3d_makeTemplate((int)kind.i, (int)detail.i);
    return m ? fire_s3d_meshHandle(m) : Int(0);
}

/* A mesh from the text of a Wavefront OBJ file; 0 when there is nothing usable in it. */
static Value fire_s3d_MeshFromObj(Value text) {
    const Str* s;
    char* ascii;
    uint32_t i;
    S3dMesh* m;
    if (text.kind != K_String) return Int(0);
    s = strOf(text);
    ascii = (char*)S3D_MALLOC((s3d_size)s->length + 1);
    if (ascii == NULL) return Int(0);
    for (i = 0; i < s->length; i++) ascii[i] = s->data[i] < 128 ? (char)s->data[i] : '?';
    ascii[s->length] = 0;
    m = s3d_parseObj(ascii, s->length);
    S3D_FREE(ascii);
    return m ? fire_s3d_meshHandle(m) : Int(0);
}

/* Another user of the mesh (an object that shares the mesh of a model): the mesh lives until every user has released it. */
static Value fire_s3d_MeshRetain(Value h) {
    S3dMesh* m = (S3dMesh*)fire_s3d_find(&g_s3dMeshes, h);
    if (m) m->refs++;
    return Bool(m != NULL);
}

static Value fire_s3d_MeshFree(Value h) {
    S3dMesh* m = (S3dMesh*)fire_s3d_find(&g_s3dMeshes, h);
    if (m && --m->refs <= 0) { s3d_meshFree(m); g_s3dMeshes.items[h.i] = NULL; }
    return Bool(true);
}

static Value fire_s3d_MeshTriangleCount(Value h) {
    S3dMesh* m = (S3dMesh*)fire_s3d_find(&g_s3dMeshes, h);
    return Int(m ? (int64_t)(m->idxN / 3) : 0);
}

static Value fire_s3d_texHandle(S3dTexture* t) {
    int64_t h;
    s3d_texPrepare(t);
    t->refs = 1;
    t->strength = 1.0f;
    h = fire_s3d_add(&g_s3dTextures, t);
    if (h == 0) s3d_texFree(t);
    return Int(h);
}

/* A texture from `width * height * 4` bytes (R, G, B, A); copied. Returns the handle, 0 on bad data. */
static Value fire_s3d_TexNew(Value width, Value height, Value buffer) {
    s3d_size n;
    S3dTexture* tex;
    FireBytes in;
    if (!bytesArg(buffer, "Scene3D.Texture", &in)) return Undef();
    if (width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384) return Int(0);
    n = (s3d_size)width.i * (s3d_size)height.i * 4;
    if ((s3d_size)in.length < n) return Int(0);
    tex = (S3dTexture*)s3d_calloc(1, sizeof(S3dTexture));
    if (tex == NULL) return Int(0);
    tex->rgba = (uint8_t*)S3D_MALLOC(n);
    if (tex->rgba == NULL) { S3D_FREE(tex); return Int(0); }
    tex->w = (int)width.i; tex->h = (int)height.i;
    S3D_COPY(tex->rgba, in.data, n);
    return fire_s3d_texHandle(tex);
}

/* A checker board of `cells` x `cells` squares in two colours (0xAABBGGRR), in a picture of `size` x `size` pixels. */
static Value fire_s3d_TexChecker(Value size, Value cells, Value colorA, Value colorB) {
    S3dTexture* tex;
    int n, x, y;
    if (size.i <= 0 || size.i > 4096 || cells.i <= 0 || cells.i > size.i) return Int(0);
    tex = (S3dTexture*)s3d_calloc(1, sizeof(S3dTexture));
    if (tex == NULL) return Int(0);
    n = (int)size.i;
    tex->w = tex->h = n;
    tex->rgba = (uint8_t*)S3D_MALLOC((s3d_size)n * (s3d_size)n * 4);
    if (tex->rgba == NULL) { S3D_FREE(tex); return Int(0); }
    for (y = 0; y < n; y++)
        for (x = 0; x < n; x++) {
            uint32_t c = ((x * cells.i / n + y * cells.i / n) & 1) ? (uint32_t)colorB.i : (uint32_t)colorA.i;
            uint8_t* p = &tex->rgba[((s3d_size)y * (s3d_size)n + (s3d_size)x) * 4];
            p[0] = (uint8_t)(c & 255); p[1] = (uint8_t)((c >> 8) & 255); p[2] = (uint8_t)((c >> 16) & 255); p[3] = (uint8_t)((c >> 24) & 255);
        }
    return fire_s3d_texHandle(tex);
}

/* Gives a texture a bump map made from its own brightness. */
static Value fire_s3d_TexBumpAuto(Value h) {
    S3dTexture* t = (S3dTexture*)fire_s3d_find(&g_s3dTextures, h);
    return Bool(t != NULL && s3d_texBumpFromColors(t));
}

/* Gives a texture a bump map from a picture: `channels` bytes per pixel (1: heights; 3 or 4: colours, whose brightness is the height); the picture is scaled to the texture. */
static Value fire_s3d_TexBump(Value h, Value width, Value height, Value channels, Value buffer) {
    S3dTexture* t;
    FireBytes in;
    if (!bytesArg(buffer, "Scene3D.Texture.SetBump", &in)) return Undef();
    t = (S3dTexture*)fire_s3d_find(&g_s3dTextures, h);
    if (!t || width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384 || (channels.i != 1 && channels.i != 3 && channels.i != 4)) return Bool(false);
    if ((s3d_size)in.length < (s3d_size)width.i * (s3d_size)height.i * (s3d_size)channels.i) return Bool(false);
    return Bool(s3d_texSetBump(t, in.data, (int)width.i, (int)height.i, (int)channels.i) != 0);
}

/* Takes the bump map away again. */
static Value fire_s3d_TexBumpClear(Value h) {
    S3dTexture* t = (S3dTexture*)fire_s3d_find(&g_s3dTextures, h);
    if (!t) return Bool(false);
    s3d_texClearBump(t);
    return Bool(true);
}

/* A user of the texture lets go of it (its fire object, or an object that showed it): it goes with the last one. */
static void fire_s3d_texRelease(int64_t h) {
    S3dTexture* t = h > 0 && (s3d_size)h < g_s3dTextures.count ? (S3dTexture*)g_s3dTextures.items[h] : NULL;
    if (t && --t->refs <= 0) { s3d_texFree(t); g_s3dTextures.items[h] = NULL; }
}
static Value fire_s3d_TexFree(Value h) { fire_s3d_texRelease(h.i); return Bool(true); }

/* Whether the texture has a bump map; its strength (Texture.BumpStrength). */
static Value fire_s3d_TexHasBump(Value h) {
    S3dTexture* t = (S3dTexture*)fire_s3d_find(&g_s3dTextures, h);
    return Bool(t != NULL && t->bump != NULL);
}
static Value fire_s3d_TexStrength(Value h) {
    S3dTexture* t = (S3dTexture*)fire_s3d_find(&g_s3dTextures, h);
    return Float(t ? (Real)t->strength : (Real)1);
}
static Value fire_s3d_TexSetStrength(Value h, Value v) {
    S3dTexture* t = (S3dTexture*)fire_s3d_find(&g_s3dTextures, h);
    if (t) t->strength = (float)toR(v);
    return Bool(t != NULL);
}

/* ---- the scene: objects, lights, cameras, materials and scenes live here as items (the fire objects hold their handles) --------------------------------------------------------
 * An item is a block of numbers (`f`: floats, `n`: integers and flags; the slots are listed below and in the prelude) and what its kind needs besides. The properties of the fire
 * classes read and write the slots; Render draws a scene in one call: the objects, lights and settings are taken from here, nothing is put together in fire for every picture.
 * An item lives while someone uses it: its fire object (until its destructor: FIRE_S3D_ITEM_ALIVE is cleared, a scene skips it), the scenes it is in, the objects that use a
 * material. A mesh and a texture are counted the same way (an object that shows them is a user). */
enum { FIRE_S3D_OBJECT = 1, FIRE_S3D_LIGHT = 2, FIRE_S3D_CAMERA = 3, FIRE_S3D_SCENE = 4, FIRE_S3D_MATERIAL = 5 };
#define FIRE_S3D_FLOATS 19
#define FIRE_S3D_INTS 8

/* object: f = x, y, z, rotation x, y, z, scale x, y, z, anchor x, y, z, texture x, y, width, height, rotation, opacity, alpha cutoff;
 *         n = colour, visible, two sided, lit, casts shadows, receives shadows, layers, billboard */
enum { OF_PX = 0, OF_RX = 3, OF_SX = 6, OF_AX = 9, OF_TX = 12, OF_TY, OF_TW, OF_TH, OF_TROT, OF_OPACITY, OF_CUTOFF };
enum { ON_COLOR = 0, ON_VISIBLE, ON_TWOSIDED, ON_LIT, ON_CAST, ON_RECEIVE, ON_LAYERS, ON_BILLBOARD };
/* light: f = intensity, x, y, z, direction x, y, z, range; n = kind, colour, enabled, casts shadows, layers */
enum { LF_INTENSITY = 0, LF_X, LF_Y, LF_Z, LF_DX, LF_DY, LF_DZ, LF_RANGE };
enum { LN_KIND = 0, LN_COLOR, LN_ENABLED, LN_CAST, LN_LAYERS };
/* camera: f = x, y, z, rotation x, y, z, field of view, near, far; n = background, direct drawing */
enum { CF_X = 0, CF_RX = 3, CF_FOV = 6, CF_NEAR, CF_FAR };
enum { CN_BACKGROUND = 0, CN_DIRECT, CN_LAST };   /* (CN_LAST: who drew the last picture - 0 software, 1 GPU, 2 GPU into the windows, 3 the lean renderer) */
/* scene: f = ambient intensity; n = ambient colour, shadows, shadow resolution, threads, renderer */
enum { SN_AMBIENT = 0, SN_SHADOWS, SN_SHADOWRES, SN_THREADS, SN_RENDERER };
/* material: f = specular, shininess; n = smooth, two sided, emissive */
enum { MN_SMOOTH = 0, MN_TWOSIDED, MN_EMISSIVE };

typedef struct FireS3dItem {
    int kind, refs, alive;
    Real f[FIRE_S3D_FLOATS];
    int64_t n[FIRE_S3D_INTS];
    int64_t mesh, tex, mat;          /* object: the handles it uses (it is one of their users) */
    S3dMesh* tris; int trisDirty;    /* object: the triangles of AddTriangle (made into its mesh when it is needed) */
    S3dFrame* frame; int directOn;   /* camera: its depth buffer and queue; whether it draws straight into the windows */
    S3dLean* lean;                   /* camera: the lean renderer (RenderMode.Fast) */
    int64_t* objects; s3d_size objectN, objectCap;   /* scene: its objects and lights, in the order they were added */
    int64_t* lights; s3d_size lightN, lightCap;
    float* terrain; int tCols, tRows, tHard; float tCell, tStep;   /* object, a terrain: its heights (in steps), cells, how it is built */
} FireS3dItem;
static FireS3dTable g_s3dItems;

static FireS3dItem* fire_s3d_itemAt(int64_t h, int kind) {
    FireS3dItem* it = h > 0 && (s3d_size)h < g_s3dItems.count ? (FireS3dItem*)g_s3dItems.items[h] : NULL;
    return it && (kind == 0 || it->kind == kind) ? it : NULL;
}
static void fire_s3d_meshRelease(int64_t h) {
    S3dMesh* m = h > 0 && (s3d_size)h < g_s3dMeshes.count ? (S3dMesh*)g_s3dMeshes.items[h] : NULL;
    if (m && --m->refs <= 0) { s3d_meshFree(m); g_s3dMeshes.items[h] = NULL; }
}
static void fire_s3d_itemRelease(int64_t h);
static void fire_s3d_itemDestroy(int64_t h, FireS3dItem* it) {
    s3d_size i;
    g_s3dItems.items[h] = NULL;
    fire_s3d_meshRelease(it->mesh);
    fire_s3d_texRelease(it->tex);
    if (it->mat) fire_s3d_itemRelease(it->mat);
    if (it->tris) s3d_meshFree(it->tris);
#ifndef FIRE_S3D_LEAN_ONLY
    if (it->frame) s3d_frameFree(it->frame);
#endif
    if (it->lean) s3d_leanFree(it->lean);
    for (i = 0; i < it->objectN; i++) fire_s3d_itemRelease(it->objects[i]);
    for (i = 0; i < it->lightN; i++) fire_s3d_itemRelease(it->lights[i]);
    S3D_FREE(it->objects); S3D_FREE(it->lights); S3D_FREE(it->terrain);
    S3D_FREE(it);
}
static void fire_s3d_itemRelease(int64_t h) {
    FireS3dItem* it = fire_s3d_itemAt(h, 0);
    if (it && --it->refs <= 0) fire_s3d_itemDestroy(h, it);
}

/* A new item of a kind (FIRE_S3D_*) with the defaults of its fire class; 0 when the memory is gone. */
static Value fire_s3d_New(Value kind) {
    FireS3dItem* it;
    int64_t h;
    if (kind.i < FIRE_S3D_OBJECT || kind.i > FIRE_S3D_MATERIAL) return Int(0);
    it = (FireS3dItem*)s3d_calloc(1, sizeof(FireS3dItem));
    if (it == NULL) return Int(0);
    it->kind = (int)kind.i; it->refs = 1; it->alive = 1;
    switch (it->kind) {
        case FIRE_S3D_OBJECT:
            it->f[OF_SX] = it->f[OF_SX + 1] = it->f[OF_SX + 2] = 1;
            it->f[OF_AX] = it->f[OF_AX + 1] = it->f[OF_AX + 2] = (Real)0.5;
            it->f[OF_OPACITY] = 1;
            it->n[ON_COLOR] = 4291611852LL; it->n[ON_VISIBLE] = 1; it->n[ON_LIT] = 1; it->n[ON_CAST] = 1; it->n[ON_RECEIVE] = 1; it->n[ON_LAYERS] = 16777215;
            break;
        case FIRE_S3D_LIGHT:
            it->f[LF_INTENSITY] = 1; it->f[LF_Y] = 2; it->f[LF_DX] = (Real)-0.5; it->f[LF_DY] = -1; it->f[LF_DZ] = (Real)-0.5; it->f[LF_RANGE] = 10;
            it->n[LN_KIND] = S3D_LIGHT_POINT; it->n[LN_COLOR] = 4294967295LL; it->n[LN_ENABLED] = 1; it->n[LN_CAST] = 1; it->n[LN_LAYERS] = 16777215;
            break;
        case FIRE_S3D_CAMERA:
            it->f[CF_FOV] = (Real)1.0471975511965976; it->f[CF_NEAR] = (Real)0.1; it->f[CF_FAR] = 500;
            it->n[CN_BACKGROUND] = 4282198552LL;
            break;
        case FIRE_S3D_SCENE:
            it->f[0] = (Real)0.25;
            it->n[SN_AMBIENT] = 4294967295LL; it->n[SN_SHADOWS] = 1; it->n[SN_SHADOWRES] = 1024;
            break;
        default:   /* material */
            it->f[1] = 32; it->n[MN_SMOOTH] = 1;
            break;
    }
    h = fire_s3d_add(&g_s3dItems, it);
    if (h == 0) S3D_FREE(it);
    return Int(h);
}

/* The fire object is gone: the item is no longer drawn, and it goes when nobody else uses it. */
static Value fire_s3d_Free(Value h) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, 0);
    if (it) { it->alive = 0; fire_s3d_itemRelease(h.i); }
    return Bool(true);
}

static Value fire_s3d_GetF(Value h, Value slot) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, 0);
    return Float(it && slot.i >= 0 && slot.i < FIRE_S3D_FLOATS ? it->f[slot.i] : (Real)0);
}
static Value fire_s3d_SetF(Value h, Value slot, Value v) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, 0);
    if (!it || slot.i < 0 || slot.i >= FIRE_S3D_FLOATS) return Bool(false);
    it->f[slot.i] = toR(v);
    return Bool(true);
}
/* Three slots at once (a position, a rotation, ...); `add`: added to them. */
static Value fire_s3d_SetF3(Value h, Value slot, Value x, Value y, Value z, Value add) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, 0);
    if (!it || slot.i < 0 || slot.i + 3 > FIRE_S3D_FLOATS) return Bool(false);
    if (add.i) { it->f[slot.i] = it->f[slot.i] + toR(x); it->f[slot.i + 1] = it->f[slot.i + 1] + toR(y); it->f[slot.i + 2] = it->f[slot.i + 2] + toR(z); }
    else { it->f[slot.i] = toR(x); it->f[slot.i + 1] = toR(y); it->f[slot.i + 2] = toR(z); }
    return Bool(true);
}
static Value fire_s3d_GetI(Value h, Value slot) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, 0);
    return Int(it && slot.i >= 0 && slot.i < FIRE_S3D_INTS ? it->n[slot.i] : 0);
}
static Value fire_s3d_GetB(Value h, Value slot) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, 0);
    return Bool(it && slot.i >= 0 && slot.i < FIRE_S3D_INTS && it->n[slot.i] != 0);
}
static Value fire_s3d_SetI(Value h, Value slot, Value v) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, 0);
    if (!it || slot.i < 0 || slot.i >= FIRE_S3D_INTS) return Bool(false);
    it->n[slot.i] = v.kind == K_Float ? (int64_t)v.f : v.kind == K_Bool ? (v.i ? 1 : 0) : v.i;
    return Bool(true);
}

/* ---- objects ---- */
/* The mesh of an object: it becomes one of the mesh's users (0: none). */
static Value fire_s3d_ObjMesh(Value h, Value mesh) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    S3dMesh* m = mesh.i > 0 ? (S3dMesh*)fire_s3d_find(&g_s3dMeshes, mesh) : NULL;
    if (!it || (mesh.i > 0 && !m)) return Bool(false);
    if (m) m->refs++;
    fire_s3d_meshRelease(it->mesh);
    it->mesh = m ? mesh.i : 0;
    return Bool(true);
}
static Value fire_s3d_ObjTexture(Value h, Value tex) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    S3dTexture* t = tex.i > 0 ? (S3dTexture*)fire_s3d_find(&g_s3dTextures, tex) : NULL;
    if (!it) return Bool(false);
    if (t) t->refs++;
    fire_s3d_texRelease(it->tex);
    it->tex = t ? tex.i : 0;
    return Bool(true);
}
static Value fire_s3d_ObjMaterial(Value h, Value mat) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    FireS3dItem* m = fire_s3d_itemAt(mat.i, FIRE_S3D_MATERIAL);
    if (!it) return Bool(false);
    if (m) m->refs++;
    if (it->mat) fire_s3d_itemRelease(it->mat);
    it->mat = m ? mat.i : 0;
    return Bool(true);
}

/* The triangles of AddTriangle become the mesh of the object (when they changed). */
static S3dMesh* fire_s3d_objMesh(FireS3dItem* it) {
    if (it->trisDirty && it->tris) {
        S3dMesh* src = it->tris;
        S3dMesh* m = s3d_meshNew();
        int64_t h;
        it->trisDirty = 0;
        if (m == NULL || !S3D_GROW(m->pos, m->posCap, src->posN) || !S3D_GROW(m->uv, m->uvCap, src->uvN) || !S3D_GROW(m->idx, m->idxCap, src->idxN)) { s3d_meshFree(m); return NULL; }
        S3D_COPY(m->pos, src->pos, src->posN * sizeof(float)); m->posN = src->posN;
        S3D_COPY(m->uv, src->uv, src->uvN * sizeof(float)); m->uvN = src->uvN;
        S3D_COPY(m->idx, src->idx, src->idxN * sizeof(uint32_t)); m->idxN = src->idxN;
        if (!s3d_meshFinish(m)) { s3d_meshFree(m); return NULL; }
        h = fire_s3d_add(&g_s3dMeshes, m);
        if (h == 0) { s3d_meshFree(m); return NULL; }
        fire_s3d_meshRelease(it->mesh);
        it->mesh = h;
    }
    return it->mesh > 0 ? (S3dMesh*)g_s3dMeshes.items[it->mesh] : NULL;
}

/* A triangle of an empty object (meters; counter clockwise seen from outside): a corner at a time, every third one closes a triangle. */
static Value fire_s3d_ObjCorner(Value h, Value x, Value y, Value z) {
    static const float uv[6] = { 0, 1, 1, 1, 0, 0 };
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    S3dMesh* m;
    s3d_size k;
    if (!it || (it->mesh != 0 && it->tris == NULL)) return Bool(false);   /* (only an empty object takes triangles) */
    if (it->tris == NULL && (it->tris = s3d_meshNew()) == NULL) return Bool(false);
    m = it->tris;
    k = m->posN / 3;
    s3d_addVertex(m, (float)toR(x), (float)toR(y), (float)toR(z), uv[(k % 3) * 2], uv[(k % 3) * 2 + 1]);
    if (k % 3 == 2) s3d_addTriangle(m, (uint32_t)(k - 2), (uint32_t)(k - 1), (uint32_t)k);
    it->trisDirty = 1;
    return Bool(!m->bad);
}
static int fire_s3d_objectOf(FireS3dItem* o, S3dMesh** mesh, S3dTexture** texture, S3dObjectParams* opOut, S3dSurface* sOut);

/* ---- terrains (scene3d.terrain): a mesh from a grid of heights ----------------------------------------------------------------------------------------------------------
 * The grid has `cols` x `rows` heights (in steps: a height of 1 is `step` meters). Smooth: every height is a corner, the cells between them are two triangles with shared corners (the
 * normals are smoothed); the diagonal of a cell goes from (i+1, j) to (i, j+1). Hard: every height is a block of `cell` x `cell` - a flat top at its height and upright walls only
 * where a neighbour is lower (the border goes down to the lowest of 0 and the heights): what cannot be seen is not built; neighbouring tops of the same height are one rectangle,
 * walls in a line with the same heights on both sides one quad. The terrain lies centred on x and z, height 0 at y = 0 (its anchor). Texture coordinates follow the ground: u across
 * the width, v across the depth (measured like the object's height, so that TextureWidth / TextureHeight tile in meters). */
typedef struct FireS3dTerrainBuild { S3dMesh* m; float x0, z0, uScale, vScale, yTop; } FireS3dTerrainBuild;

static void fire_s3d_quad(FireS3dTerrainBuild* b, const float* p /* 4 corners x, y, z: counter clockwise seen from the front */, int wall, int alongX) {
    uint32_t base = (uint32_t)(b->m->posN / 3);
    int k;
    for (k = 0; k < 4; k++) {
        float x = p[k * 3], y = p[k * 3 + 1], z = p[k * 3 + 2], u, v;
        if (!wall) { u = (x - b->x0) / b->uScale; v = (z - b->z0) / b->vScale; }
        else { u = (alongX ? x - b->x0 : z - b->z0) / b->uScale; v = (b->yTop - y) / b->vScale; }
        s3d_addVertex(b->m, x, y, z, u, v);
    }
    s3d_addTriangle(b->m, base, base + 1, base + 2);
    s3d_addTriangle(b->m, base, base + 2, base + 3);
}

static S3dMesh* fire_s3d_terrainMesh(const float* H, int cols, int rows, float cell, float step, int hard) {
    FireS3dTerrainBuild b;
    float minH = H[0], maxH = H[0], W, D, height;
    int i, j;
    s3d_size n = (s3d_size)cols * (s3d_size)rows, k;
    for (k = 1; k < n; k++) { if (H[k] < minH) minH = H[k]; if (H[k] > maxH) maxH = H[k]; }
    b.m = s3d_meshNew();
    if (b.m == NULL) return NULL;
    W = (float)(hard ? cols : cols - 1) * cell; D = (float)(hard ? rows : rows - 1) * cell;
    b.x0 = -W * 0.5f; b.z0 = -D * 0.5f;
    if (hard && minH > 0) minH = 0;
    height = (maxH - minH) * step;
    b.uScale = W > 0 ? W : 1;
    b.vScale = height >= 0.000001f ? height : (D > 0 ? D : 1);
    b.yTop = maxH * step;
    if (!hard) {
        for (j = 0; j < rows; j++)
            for (i = 0; i < cols; i++) {
                float x = b.x0 + (float)i * cell, z = b.z0 + (float)j * cell;
                s3d_addVertex(b.m, x, H[(s3d_size)j * cols + i] * step, z, (x - b.x0) / b.uScale, (z - b.z0) / b.vScale);
            }
        for (j = 0; j + 1 < rows; j++)
            for (i = 0; i + 1 < cols; i++) {
                uint32_t a = (uint32_t)((s3d_size)j * cols + i), r = a + 1, d = a + (uint32_t)cols, rd = d + 1;
                s3d_addTriangle(b.m, a, d, r);
                s3d_addTriangle(b.m, r, d, rd);
            }
    } else {
        uint8_t* done = (uint8_t*)s3d_calloc(n, 1);
        if (done == NULL) { s3d_meshFree(b.m); return NULL; }
        /* the tops: rectangles of the same height (greedy: as wide as it goes, then as deep as the whole width goes) */
        for (j = 0; j < rows; j++)
            for (i = 0; i < cols; i++) {
                float h = H[(s3d_size)j * cols + i], p[12], y, xa, xb, za, zb;
                int w = 1, d = 1, ok = 1, t;
                if (done[(s3d_size)j * cols + i]) continue;
                while (i + w < cols && !done[(s3d_size)j * cols + i + w] && H[(s3d_size)j * cols + i + w] == h) w++;
                while (ok && j + d < rows) {
                    for (t = 0; t < w; t++) if (done[(s3d_size)(j + d) * cols + i + t] || H[(s3d_size)(j + d) * cols + i + t] != h) { ok = 0; break; }
                    if (ok) d++;
                }
                for (k = 0; k < (s3d_size)d; k++) for (t = 0; t < w; t++) done[(s3d_size)(j + (int)k) * cols + i + t] = 1;
                y = h * step; xa = b.x0 + (float)i * cell; xb = xa + (float)w * cell; za = b.z0 + (float)j * cell; zb = za + (float)d * cell;
                p[0] = xa; p[1] = y; p[2] = za;  p[3] = xa; p[4] = y; p[5] = zb;  p[6] = xb; p[7] = y; p[8] = zb;  p[9] = xb; p[10] = y; p[11] = za;
                fire_s3d_quad(&b, p, 0, 0);
            }
        S3D_FREE(done);
        /* the walls across x (between column i-1 and i, at x = x0 + i * cell): runs along z with the same two heights */
        for (i = 0; i <= cols; i++)
            for (j = 0; j < rows; ) {
                float l = i > 0 ? H[(s3d_size)j * cols + i - 1] : minH, r = i < cols ? H[(s3d_size)j * cols + i] : minH;
                int run = 1;
                if (l == r) { j++; continue; }
                while (j + run < rows && (i > 0 ? H[(s3d_size)(j + run) * cols + i - 1] : minH) == l && (i < cols ? H[(s3d_size)(j + run) * cols + i] : minH) == r) run++;
                {
                    float x = b.x0 + (float)i * cell, za = b.z0 + (float)j * cell, zb = za + (float)run * cell, lo = (l < r ? l : r) * step, hi = (l < r ? r : l) * step, p[12];
                    if (l > r) {   /* the left one is higher: the wall faces +x */
                        p[0] = x; p[1] = lo; p[2] = zb;  p[3] = x; p[4] = lo; p[5] = za;  p[6] = x; p[7] = hi; p[8] = za;  p[9] = x; p[10] = hi; p[11] = zb;
                    } else {
                        p[0] = x; p[1] = lo; p[2] = za;  p[3] = x; p[4] = lo; p[5] = zb;  p[6] = x; p[7] = hi; p[8] = zb;  p[9] = x; p[10] = hi; p[11] = za;
                    }
                    fire_s3d_quad(&b, p, 1, 0);
                }
                j += run;
            }
        /* the walls across z (between row j-1 and j): runs along x */
        for (j = 0; j <= rows; j++)
            for (i = 0; i < cols; ) {
                float f = j > 0 ? H[(s3d_size)(j - 1) * cols + i] : minH, bk = j < rows ? H[(s3d_size)j * cols + i] : minH;
                int run = 1;
                if (f == bk) { i++; continue; }
                while (i + run < cols && (j > 0 ? H[(s3d_size)(j - 1) * cols + i + run] : minH) == f && (j < rows ? H[(s3d_size)j * cols + i + run] : minH) == bk) run++;
                {
                    float z = b.z0 + (float)j * cell, xa = b.x0 + (float)i * cell, xb = xa + (float)run * cell, lo = (f < bk ? f : bk) * step, hi = (f < bk ? bk : f) * step, p[12];
                    if (f > bk) {   /* the row before is higher: the wall faces +z */
                        p[0] = xa; p[1] = lo; p[2] = z;  p[3] = xb; p[4] = lo; p[5] = z;  p[6] = xb; p[7] = hi; p[8] = z;  p[9] = xa; p[10] = hi; p[11] = z;
                    } else {
                        p[0] = xb; p[1] = lo; p[2] = z;  p[3] = xa; p[4] = lo; p[5] = z;  p[6] = xa; p[7] = hi; p[8] = z;  p[9] = xb; p[10] = hi; p[11] = z;
                    }
                    fire_s3d_quad(&b, p, 1, 1);
                }
                i += run;
            }
    }
    if (!s3d_meshFinish(b.m)) { s3d_meshFree(b.m); return NULL; }
    return b.m;
}

/* Makes the object a terrain: `heights` is a byte buffer (cols * rows heights 0..255, or cols * rows * 4 bytes R, G, B, A whose brightness is the height) or an array of numbers;
 * `cell` the size of a cell (meters), `step` the height of 1 (meters), `hard` 0 smooth / 1 blocks. false: the data do not fit (or the memory is gone). */
static Value fire_s3d_Terrain(Value h, Value heights, Value cols, Value rows, Value cell, Value step, Value hard) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    s3d_size n, k;
    float* H;
    S3dMesh* m;
    int64_t mh;
    if (!it || cols.i < (hard.i ? 1 : 2) || rows.i < (hard.i ? 1 : 2) || cols.i > 4096 || rows.i > 4096 || toR(cell) <= 0) return Bool(false);
    n = (s3d_size)cols.i * (s3d_size)rows.i;
    H = (float*)S3D_MALLOC(n * sizeof(float));
    if (H == NULL) return Bool(false);
    if (heights.kind == K_Array) {
        Arr* a = arrOf(heights);
        const Value* v;
        if ((s3d_size)a->length != n) { S3D_FREE(H); return Bool(false); }
        v = arrItems(a);
        for (k = 0; k < n; k++) H[k] = (float)toR(v[k]);
    } else {
        FireBytes in;
        if (!bytesArg(heights, "Scene3D.Terrain", &in)) { S3D_FREE(H); return Undef(); }
        if ((s3d_size)in.length == n) for (k = 0; k < n; k++) H[k] = (float)in.data[k];
        else if ((s3d_size)in.length == n * 4) for (k = 0; k < n; k++) H[k] = (float)(((int)in.data[k * 4] * 3 + (int)in.data[k * 4 + 1] * 6 + (int)in.data[k * 4 + 2]) / 10);
        else { S3D_FREE(H); return Bool(false); }
    }
    m = fire_s3d_terrainMesh(H, (int)cols.i, (int)rows.i, (float)toR(cell), (float)toR(step), hard.i != 0);
    if (m == NULL || (mh = fire_s3d_add(&g_s3dMeshes, m)) == 0) { if (m) s3d_meshFree(m); S3D_FREE(H); return Bool(false); }
    fire_s3d_meshRelease(it->mesh);
    it->mesh = mh;
    S3D_FREE(it->terrain);
    it->terrain = H; it->tCols = (int)cols.i; it->tRows = (int)rows.i; it->tHard = hard.i != 0; it->tCell = (float)toR(cell); it->tStep = (float)toR(step);
    /* the anchor: x and z in the middle, y where the height 0 is */
    it->f[OF_AX] = (Real)0.5; it->f[OF_AX + 2] = (Real)0.5;
    it->f[OF_AX + 1] = m->bmax.y - m->bmin.y > 1e-6f ? (Real)(-m->bmin.y / (m->bmax.y - m->bmin.y)) : (Real)0;
    return Bool(true);
}

/* The height of the ground of a terrain below the world point (x, z): its y in the world (undefined outside the terrain). The terrain may be moved, scaled and turned around
 * the vertical axis. */
static Value fire_s3d_TerrainHeight(Value h, Value wx, Value wz) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    S3dMesh* m;
    S3dTexture* tex;
    S3dObjectParams op;
    S3dSurface s;
    S3dMat4 model, normalM;
    S3dCamera cam;
    float a, b, c, d, e, f, det, px, pz, lx, lz, gx, gz, y;
    int i, j, cols, rows;
    if (!it || it->terrain == NULL || !fire_s3d_objectOf(it, &m, &tex, &op, &s)) return Undef();
    memset(&cam, 0, sizeof cam);
    s.billboard = 0;
    s3d_objectMatrices(&cam, &op, &s, &model, &normalM);
    /* local x, z from world x, z: the inverse of the x/z part of the model (a turn around y and a scale) */
    a = S3D_AT(model, 0, 0); b = S3D_AT(model, 0, 2); c = S3D_AT(model, 2, 0); d = S3D_AT(model, 2, 2);
    e = (float)toR(wx) - S3D_AT(model, 0, 3); f = (float)toR(wz) - S3D_AT(model, 2, 3);
    det = a * d - b * c;
    if (det == 0) return Undef();
    lx = (d * e - b * f) / det; lz = (a * f - c * e) / det;
    cols = it->tCols; rows = it->tRows;
    if (it->tHard) {
        gx = lx / it->tCell + (float)cols * 0.5f; gz = lz / it->tCell + (float)rows * 0.5f;
        if (gx < 0 || gz < 0 || gx >= (float)cols || gz >= (float)rows) return Undef();
        y = it->terrain[(s3d_size)(int)gz * cols + (int)gx] * it->tStep;
    } else {
        float fx, fz, h00, h10, h01, h11;
        gx = lx / it->tCell + (float)(cols - 1) * 0.5f; gz = lz / it->tCell + (float)(rows - 1) * 0.5f;
        if (gx < 0 || gz < 0 || gx > (float)(cols - 1) || gz > (float)(rows - 1)) return Undef();
        i = (int)gx; j = (int)gz;
        if (i > cols - 2) i = cols - 2;
        if (j > rows - 2) j = rows - 2;
        fx = gx - (float)i; fz = gz - (float)j;
        h00 = it->terrain[(s3d_size)j * cols + i]; h10 = it->terrain[(s3d_size)j * cols + i + 1];
        h01 = it->terrain[(s3d_size)(j + 1) * cols + i]; h11 = it->terrain[(s3d_size)(j + 1) * cols + i + 1];
        /* the two triangles of the cell (the diagonal from (1, 0) to (0, 1)) */
        if (fx + fz <= 1) y = h00 + fx * (h10 - h00) + fz * (h01 - h00);
        else y = h11 + (1 - fx) * (h01 - h11) + (1 - fz) * (h10 - h11);
        y *= it->tStep;
    }
    px = lx; pz = lz;
    return Float((Real)s3d_transformPoint(&model, s3d_v3(px, y, pz)).y);
}

static Value fire_s3d_ObjTriangles(Value h) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    S3dMesh* m = it ? fire_s3d_objMesh(it) : NULL;
    return Int(m ? (int64_t)(m->idxN / 3) : 0);
}

static Real fire_s3d_extent(FireS3dItem* it, int axis) {
    S3dMesh* m = fire_s3d_objMesh(it);
    if (m == NULL) return 0;
    return axis == 0 ? (Real)m->bmax.x - (Real)m->bmin.x : axis == 1 ? (Real)m->bmax.y - (Real)m->bmin.y : (Real)m->bmax.z - (Real)m->bmin.z;
}
/* The size of the mesh along an axis (0 x, 1 y, 2 z) - `scaled`: times the scale of the object (its outer dimension, meters). */
static Value fire_s3d_ObjExtent(Value h, Value axis, Value scaled) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    Real e;
    if (!it || axis.i < 0 || axis.i > 2) return Float((Real)0);
    e = fire_s3d_extent(it, (int)axis.i);
    return Float(scaled.i ? it->f[OF_SX + axis.i] * e : e);
}
/* The outer dimension along an axis becomes the scale of that axis; false when the mesh has no extent there. */
static Value fire_s3d_ObjSize(Value h, Value axis, Value meters) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_OBJECT);
    Real e;
    if (!it || axis.i < 0 || axis.i > 2) return Bool(false);
    e = fire_s3d_extent(it, (int)axis.i);
    if (e < (Real)0.000001) return Bool(false);
    it->f[OF_SX + axis.i] = toR(meters) / e;
    return Bool(true);
}

/* ---- cameras and scenes ---- */
static Value fire_s3d_CamLookAt(Value h, Value x, Value y, Value z) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_CAMERA);
    float rx, ry;
    if (!it) return Bool(false);
    s3d_lookAngles(s3d_v3((float)it->f[CF_X], (float)it->f[CF_X + 1], (float)it->f[CF_X + 2]), s3d_v3((float)toR(x), (float)toR(y), (float)toR(z)), &rx, &ry);
    it->f[CF_RX] = (Real)rx; it->f[CF_RX + 1] = (Real)ry; it->f[CF_RX + 2] = 0;
    return Bool(true);
}
static Value fire_s3d_CamRenderer(Value h) {
    FireS3dItem* it = fire_s3d_itemAt(h.i, FIRE_S3D_CAMERA);
    return Int(it ? it->n[CN_LAST] : 0);
}

/* Adds an object or a light to a scene (the scene becomes one of its users; cameras and textures need nothing here). */
static Value fire_s3d_SceneAdd(Value scene, Value item) {
    FireS3dItem* s = fire_s3d_itemAt(scene.i, FIRE_S3D_SCENE);
    FireS3dItem* it = fire_s3d_itemAt(item.i, 0);
    if (!s || !it) return Bool(false);
    if (it->kind == FIRE_S3D_OBJECT) { if (!S3D_GROW(s->objects, s->objectCap, s->objectN + 1)) return Bool(false); s->objects[s->objectN++] = item.i; }
    else if (it->kind == FIRE_S3D_LIGHT) { if (!S3D_GROW(s->lights, s->lightCap, s->lightN + 1)) return Bool(false); s->lights[s->lightN++] = item.i; }
    else return Bool(true);
    it->refs++;
    return Bool(true);
}
static int fire_s3d_listRemove(int64_t* list, s3d_size* n, int64_t h) {
    s3d_size i;
    for (i = 0; i < *n; i++)
        if (list[i] == h) {
            for (; i + 1 < *n; i++) list[i] = list[i + 1];
            (*n)--;
            return 1;
        }
    return 0;
}
static Value fire_s3d_SceneRemove(Value scene, Value item) {
    FireS3dItem* s = fire_s3d_itemAt(scene.i, FIRE_S3D_SCENE);
    if (!s) return Bool(false);
    if (fire_s3d_listRemove(s->objects, &s->objectN, item.i) || fire_s3d_listRemove(s->lights, &s->lightN, item.i)) fire_s3d_itemRelease(item.i);
    return Bool(true);
}

static Real fire_s3d_channel(int64_t c, int shift) { return (Real)((c >> shift) & 255) / (Real)255.0; }

/* What the renderers take for an object (the numbers are worked out as the fire side did before, in Real, and then made floats - the pictures stay the same). 0: it has no mesh. */
static int fire_s3d_objectOf(FireS3dItem* o, S3dMesh** mesh, S3dTexture** texture, S3dObjectParams* opOut, S3dSurface* sOut) {
    const Real* F = o->f;
    const int64_t* N = o->n;
    S3dMesh* m = fire_s3d_objMesh(o);
    S3dTexture* tex = o->tex > 0 ? (S3dTexture*)g_s3dTextures.items[o->tex] : NULL;
    FireS3dItem* mat = o->mat ? fire_s3d_itemAt(o->mat, FIRE_S3D_MATERIAL) : NULL;
    Real minX, minY, minZ, w, hgt, d;
    Real spec = 0, shin = 32, opacity, repU = 1, repV = 1, offU = 0, offV = 0;
    int smooth = 1, two = N[ON_TWOSIDED] != 0;
    int64_t emissive = 0;
    S3dObjectParams op;
    S3dSurface s;
    if (m == NULL) return 0;
    minX = (Real)m->bmin.x; minY = (Real)m->bmin.y; minZ = (Real)m->bmin.z;
    w = (Real)m->bmax.x - minX; hgt = (Real)m->bmax.y - minY; d = (Real)m->bmax.z - minZ;
    if (mat) {
        spec = mat->f[0]; shin = mat->f[1];
        if (!mat->n[MN_SMOOTH]) smooth = 0;
        if (mat->n[MN_TWOSIDED]) two = 1;
        emissive = mat->n[MN_EMISSIVE];
    }
    if (tex) {
        Real objectWidth = F[OF_SX] * w, objectHeight = F[OF_SX + 1] * hgt, tileW, tileH;
        if (objectHeight < (Real)0.000001) objectHeight = F[OF_SX + 2] * d;
        tileW = objectWidth; tileH = objectHeight;
        if (F[OF_TW] > 0) tileW = F[OF_TW];
        if (F[OF_TH] > 0) tileH = F[OF_TH];
        if (F[OF_TW] > 0 && objectWidth > (Real)0.000001) repU = objectWidth / F[OF_TW];
        if (F[OF_TH] > 0 && objectHeight > (Real)0.000001) repV = objectHeight / F[OF_TH];
        if (tileW > (Real)0.000001) offU = F[OF_TX] / tileW;
        if (tileH > (Real)0.000001) offV = F[OF_TY] / tileH;
    }
    opacity = F[OF_OPACITY];
    if (opacity < 0) opacity = 0;
    if (opacity > 1) opacity = 1;
    op.pos = s3d_v3((float)F[OF_PX], (float)F[OF_PX + 1], (float)F[OF_PX + 2]);
    op.rx = (float)F[OF_RX]; op.ry = (float)F[OF_RX + 1]; op.rz = (float)F[OF_RX + 2];
    op.scale = s3d_v3((float)F[OF_SX], (float)F[OF_SX + 1], (float)F[OF_SX + 2]);
    op.pivot = s3d_v3((float)(minX + F[OF_AX] * w), (float)(minY + F[OF_AX + 1] * hgt), (float)(minZ + F[OF_AX + 2] * d));
    s = s3d_surfaceDefault();
    s.color = s3d_v3((float)fire_s3d_channel(N[ON_COLOR], 0), (float)fire_s3d_channel(N[ON_COLOR], 8), (float)fire_s3d_channel(N[ON_COLOR], 16));
    s.specular = (float)spec; s.shininess = (float)shin;
    s.smooth = smooth; s.twoSided = two;
    s.emissive = s3d_v3((float)fire_s3d_channel(emissive, 0), (float)fire_s3d_channel(emissive, 8), (float)fire_s3d_channel(emissive, 16));
    s.useTexture = tex != NULL;
    {   /* the texture mapping: scale by the repeats, rotate, move: u' = c*ru*u - s*rv*v + ou ; v' = s*ru*u + c*rv*v + ov */
        float ru = (float)repU, rv = (float)repV, rot = (float)F[OF_TROT], ou = (float)offU, ov = (float)offV;
        float cs = cosf(rot), sn = sinf(rot);
        s.tm[0] = cs * ru; s.tm[1] = -sn * rv; s.tm[2] = ou;
        s.tm[3] = sn * ru; s.tm[4] = cs * rv; s.tm[5] = ov;
    }
    s.opacity = (float)opacity; s.cutoff = (float)F[OF_CUTOFF];
    s.lit = N[ON_LIT] != 0; s.castShadows = N[ON_CAST] != 0; s.receiveShadows = N[ON_RECEIVE] != 0;
    s.layers = (int)(float)N[ON_LAYERS];
    s.bump = tex && tex->bump ? tex->strength : 0.0f;
    s.billboard = (int)N[ON_BILLBOARD];
    *mesh = m; *texture = tex; *opOut = op; *sOut = s;
    return 1;
}

/* The full renderer queues an object into `f`, the lean one (`L`) transforms and lights it at once. */
static void fire_s3d_queueObject(S3dFrame* f, S3dLean* L, const S3dCamera* cam, FireS3dItem* o) {
    S3dMesh* m;
    S3dTexture* tex;
    S3dObjectParams op;
    S3dSurface s;
    if (!fire_s3d_objectOf(o, &m, &tex, &op, &s)) return;
#ifdef FIRE_S3D_LEAN_ONLY
    (void)f; (void)cam;
    if (L) s3d_leanObject(L, m, &op, &s, tex);
#else
    if (L) s3d_leanObject(L, m, &op, &s, tex);
    else s3d_queueMesh(f, cam, m, &op, &s, tex);
#endif
}

/* The lean renderer's shadow map covers the objects that cast shadows (their boxes in the world). */
static void fire_s3d_leanShadows(S3dLean* L, const S3dCamera* cam, const FireS3dItem* scene) {
    S3dVec3 lo = s3d_v3(1e30f, 1e30f, 1e30f), hi = s3d_v3(-1e30f, -1e30f, -1e30f);
    s3d_size i;
    if (L->shadowLight < 0) return;
    for (i = 0; i < scene->objectN; i++) {
        FireS3dItem* o = fire_s3d_itemAt(scene->objects[i], FIRE_S3D_OBJECT);
        S3dMesh* m;
        S3dTexture* tex;
        S3dObjectParams op;
        S3dSurface s;
        S3dMat4 model, normalM;
        int c;
        if (!o || !o->alive || !o->n[ON_VISIBLE] || !fire_s3d_objectOf(o, &m, &tex, &op, &s) || !s.castShadows || s.opacity < 1) continue;
        s3d_objectMatrices(cam, &op, &s, &model, &normalM);
        for (c = 0; c < 8; c++) {
            S3dVec3 q = s3d_transformPoint(&model, s3d_v3((c & 1) ? m->bmax.x : m->bmin.x, (c & 2) ? m->bmax.y : m->bmin.y, (c & 4) ? m->bmax.z : m->bmin.z));
            lo = s3d_v3(s3d_minf(lo.x, q.x), s3d_minf(lo.y, q.y), s3d_minf(lo.z, q.z));
            hi = s3d_v3(s3d_maxf(hi.x, q.x), s3d_maxf(hi.y, q.y), s3d_maxf(hi.z, q.z));
        }
    }
    s3d_leanShadows(L, lo, hi, (int)(float)scene->n[SN_SHADOWRES]);
}



static void fire_s3d_lightOf(S3dLight* light, int64_t kind, int64_t color, Real intensity, const Real* pos, const Real* dir, Real range, int shadows, int64_t layers) {
    light->kind = (int)kind;
    light->color = s3d_v3((float)fire_s3d_channel(color, 0), (float)fire_s3d_channel(color, 8), (float)fire_s3d_channel(color, 16));
    light->intensity = (float)intensity;
    light->pos = s3d_v3((float)pos[0], (float)pos[1], (float)pos[2]);
    light->dir = s3d_normalize(s3d_v3((float)dir[0], (float)dir[1], (float)dir[2]));
    light->range = (float)range;
    light->castShadows = shadows;
    light->mask = (int)(float)layers;
}

static S3dCamera fire_s3d_cameraOf(const FireS3dItem* c) {
    S3dCamera cam;
    cam.pos = s3d_v3((float)c->f[CF_X], (float)c->f[CF_X + 1], (float)c->f[CF_X + 2]);
    cam.rx = (float)c->f[CF_RX]; cam.ry = (float)c->f[CF_RX + 1]; cam.rz = (float)c->f[CF_RX + 2];
    cam.fovY = (float)c->f[CF_FOV]; cam.nearPlane = (float)c->f[CF_NEAR]; cam.farPlane = (float)c->f[CF_FAR];
    return cam;
}

/* The lights of a scene: its ambient light first, then the ones that are on (`*n` of them); NULL when the memory is gone. */
static S3dLight* fire_s3d_lightsOf(const FireS3dItem* s, s3d_size* n) {
    static const Real zero3[3] = { 0, 0, 0 };
    S3dLight* ls = (S3dLight*)S3D_MALLOC(sizeof(S3dLight) * (s->lightN + 1));
    s3d_size i;
    *n = 0;
    if (ls == NULL) return NULL;
    fire_s3d_lightOf(&ls[(*n)++], S3D_LIGHT_AMBIENT, s->n[SN_AMBIENT], s->f[0], zero3, zero3, 1, 0, 16777215);
    for (i = 0; i < s->lightN; i++) {
        FireS3dItem* L = fire_s3d_itemAt(s->lights[i], FIRE_S3D_LIGHT);
        if (!L || !L->alive || !L->n[LN_ENABLED]) continue;
        fire_s3d_lightOf(&ls[(*n)++], L->n[LN_KIND], L->n[LN_COLOR], L->f[LF_INTENSITY], &L->f[LF_X], &L->f[LF_DX], L->f[LF_RANGE], L->n[LN_CAST] && L->n[LN_KIND] != S3D_LIGHT_AMBIENT, L->n[LN_LAYERS]);
    }
    return ls;
}

/* Draws the scene as the camera sees it into `buffer` (width * height * 4 bytes, the picture of the camera's framebuffer `fb`). Returns who drew (0 software, 1 the GPU, 2 the GPU
 * straight into the windows of the framebuffer), -1 when the size is not usable, -2 when the frame cannot be cleared (the memory). */
static Value fire_s3d_Render(Value camera, Value scene, Value buffer, Value width, Value height, Value fb) {
#ifdef FIRE_S3D_LEAN_ONLY
    (void)camera; (void)scene; (void)buffer; (void)width; (void)height; (void)fb;
    return Int(-1);   /* (only the lean renderer here: the camera asks it) */
#else
    FireS3dItem* c = fire_s3d_itemAt(camera.i, FIRE_S3D_CAMERA);
    FireS3dItem* s = fire_s3d_itemAt(scene.i, FIRE_S3D_SCENE);
    Buf* b = bufferArg(buffer, "Scene3D.Camera.Render");
    S3dFrame* f;
    S3dCamera cam;
    S3dLight* ls;
    S3dFlushSettings fs;
    s3d_size i, n = 0;
    int direct;
    if (b == NULL) return Undef();
    if (!c || !s || width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384) return Int(-1);
    if ((s3d_size)b->length < (s3d_size)width.i * (s3d_size)height.i * 4) return Int(-1);
    if (c->frame == NULL && (c->frame = s3d_frameNew((int)width.i, (int)height.i)) == NULL) return Int(-1);
    f = c->frame;
    /* DirectDrawing: the frame draws into the windows of the framebuffer (or no longer) */
    direct = c->n[CN_DIRECT] != 0 && s->n[SN_RENDERER] == S3D_BACKEND_HARDWARE;
    if (direct != c->directOn) {
        if (direct) c->directOn = s3d_gpu_attach(f, fb.i) != 0;
        else { s3d_gpu_detach(f); c->directOn = 0; }
    }
    if (f->w != width.i || f->h != height.i) { if (!s3d_frameResize(f, (int)width.i, (int)height.i)) return Int(-2); }
    else s3d_frameClearDepth(f);
    f->clearColor = (uint32_t)c->n[CN_BACKGROUND];
    s3d_clearPixels(b->data, (int)width.i, (int)height.i, (uint32_t)c->n[CN_BACKGROUND]);
    cam = fire_s3d_cameraOf(c);
    for (i = 0; i < s->objectN; i++) {
        FireS3dItem* o = fire_s3d_itemAt(s->objects[i], FIRE_S3D_OBJECT);
        if (o && o->alive && o->n[ON_VISIBLE]) fire_s3d_queueObject(f, NULL, &cam, o);
    }
    if ((ls = fire_s3d_lightsOf(s, &n)) == NULL) return Int(-2);
    fs.shadows = s->n[SN_SHADOWS] != 0;
    fs.shadowResolution = (int)(float)s->n[SN_SHADOWRES];
    fs.threads = (int)(float)s->n[SN_THREADS];
    fs.backend = s->n[SN_RENDERER] == S3D_BACKEND_HARDWARE ? S3D_BACKEND_HARDWARE : S3D_BACKEND_SOFTWARE;
    s3d_flush(b->data, f, &cam, ls, n, &fs);
    S3D_FREE(ls);
    c->n[CN_LAST] = f->lastBackend;
    return Int(f->lastBackend);
#endif
}

/* ---- the lean renderer (RenderMode.Fast, s3d_lean.h) ---- */
/* Where its rows go: the framebuffer itself (a native build has the graphics bridge: no picture of the camera at all), or a buffer of the whole picture (the VM). */
typedef struct FireS3dRows { uint8_t* data; s3d_size rowBytes; } FireS3dRows;
static void fire_s3d_rowsToBuffer(void* ctx, int y, int rows, const uint8_t* data) {
    FireS3dRows* r = (FireS3dRows*)ctx;
    S3D_COPY(r->data + (s3d_size)y * r->rowBytes, data, (s3d_size)rows * r->rowBytes);
}
#if defined(FIRE_BRIDGE_GRAPHICS_H) && !defined(FIRE_LIBRARY)
#define FIRE_S3D_LEAN_DIRECT 1
static void fire_s3d_rowsToFramebuffer(void* ctx, int y, int rows, const uint8_t* data) {
    FireGfxFramebuffer* fb = (FireGfxFramebuffer*)ctx;
    s3d_size i, n = (s3d_size)fb->width * (s3d_size)rows;
    if (fb->indices) S3D_COPY(fb->indices + (s3d_size)y * (s3d_size)fb->width, data, n);
    else {
        uint32_t* to = fb->pixels + (s3d_size)y * (s3d_size)fb->width;
        for (i = 0; i < n; i++) to[i] = data[i * 4] | ((uint32_t)data[i * 4 + 1] << 8) | ((uint32_t)data[i * 4 + 2] << 16) | ((uint32_t)data[i * 4 + 3] << 24);
    }
}
#endif

/* What the lean renderer can do here: 1 it writes straight into a framebuffer (the camera needs no picture of its own), 2 it is the only renderer of this build (a small target). */
static Value fire_s3d_Lean(void) {
    int64_t bits = 0;
#ifdef FIRE_S3D_LEAN_DIRECT
    bits |= 1;
#endif
#ifdef FIRE_S3D_LEAN_ONLY
    bits |= 2;
#endif
    return Int(bits);
}

/* Draws the scene with the lean renderer into the framebuffer `fb` (width x height, `mode` 0 RGBA / 1 palette): straight into it (returns 3), or into `buffer` (width * height * 4
 * or width * height bytes; `palette`: the framebuffer's 1024 bytes RGBA when it has a palette; returns 0). -1: a size or handle that is not usable, -2: the memory. */
static Value fire_s3d_RenderLean(Value camera, Value scene, Value fb, Value width, Value height, Value mode, Value buffer, Value palette) {
    FireS3dItem* c = fire_s3d_itemAt(camera.i, FIRE_S3D_CAMERA);
    FireS3dItem* s = fire_s3d_itemAt(scene.i, FIRE_S3D_SCENE);
    int w = (int)width.i, h = (int)height.i, bpp = mode.i == 1 ? 1 : 4, result = 0;
    uint32_t pal[256];
    S3dLeanSink sink = fire_s3d_rowsToBuffer;
    void* ctx;
    FireS3dRows rows;
    S3dCamera cam;
    S3dLight* ls;
    s3d_size i, n = 0;
    if (!c || !s || width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384) return Int(-1);
#ifdef FIRE_S3D_LEAN_DIRECT
    if (buffer.kind == K_Undefined) {
        FireGfxTables* t = fire_gfxb_tables();
        FireGfxFramebuffer* f = fb.i > 0 && (fire_gfx_size)fb.i < t->fbCount ? t->framebuffers[(fire_gfx_size)fb.i] : NULL;
        if (f == NULL || f->width != w || f->height != h || (f->indices != NULL) != (bpp == 1)) return Int(-1);
        fire_gfx_fbCpu(f, 1);
        for (i = 0; i < 256; i++) pal[i] = f->palette.entries[i];
        sink = fire_s3d_rowsToFramebuffer; ctx = f;
        result = 3;
    } else
#endif
    {
        Buf* b = bufferArg(buffer, "Scene3D.Camera.Render");
        if (b == NULL) return Undef();
        if ((s3d_size)b->length < (s3d_size)w * (s3d_size)h * (s3d_size)bpp) return Int(-1);
        if (bpp == 1) {
            FireBytes p;
            if (!bytesArg(palette, "Scene3D.Camera.Render", &p)) return Undef();
            if (p.length < 1024) return Int(-1);
            for (i = 0; i < 256; i++) pal[i] = p.data[i * 4] | ((uint32_t)p.data[i * 4 + 1] << 8) | ((uint32_t)p.data[i * 4 + 2] << 16) | ((uint32_t)p.data[i * 4 + 3] << 24);
        }
        rows.data = b->data; rows.rowBytes = (s3d_size)w * (s3d_size)bpp;
        ctx = &rows;
    }
    if (c->lean == NULL && (c->lean = s3d_leanNew()) == NULL) return Int(-2);
    cam = fire_s3d_cameraOf(c);
    if ((ls = fire_s3d_lightsOf(s, &n)) == NULL) return Int(-2);
    if (!s3d_leanBegin(c->lean, w, h, bpp, pal, &cam, ls, n, (uint32_t)c->n[CN_BACKGROUND], s->n[SN_SHADOWS] != 0)) { S3D_FREE(ls); return Int(-2); }
    S3D_FREE(ls);
    fire_s3d_leanShadows(c->lean, &cam, s);
    for (i = 0; i < s->objectN; i++) {
        FireS3dItem* o = fire_s3d_itemAt(s->objects[i], FIRE_S3D_OBJECT);
        if (o && o->alive && o->n[ON_VISIBLE]) fire_s3d_queueObject(NULL, c->lean, &cam, o);
    }
    s3d_leanEnd(c->lean, sink, ctx);
    c->n[CN_LAST] = 3;
    return Int(result);
}

static void fire_s3d_reset(void) {
    s3d_size i;
    for (i = 1; i < g_s3dItems.count; i++) if (g_s3dItems.items[i]) fire_s3d_itemDestroy((int64_t)i, (FireS3dItem*)g_s3dItems.items[i]);
    for (i = 1; i < g_s3dMeshes.count; i++) s3d_meshFree((S3dMesh*)g_s3dMeshes.items[i]);
    for (i = 1; i < g_s3dTextures.count; i++) s3d_texFree((S3dTexture*)g_s3dTextures.items[i]);
    g_s3dItems.count = g_s3dMeshes.count = g_s3dTextures.count = 0;
    s3d_gpu_shutdown();
}

/* The GPU the hardware renderer uses ("" when there is none: no Vulkan, no device, or it is switched off); asking for it starts the backend. */
static Value fire_s3d_HardwareName(OwnList* list) {
    const char* name = s3d_gpu_deviceName();
    s3d_size len = strlen(name), i;
    Str* s = allocStr((uint32_t)len, list);
    for (i = 0; i < len; i++) strChars(s)[i] = (fire_char16)(uint8_t)name[i];
    return StrV(s);
}

/* Why there is no GPU ("" when there is one, or when nobody asked yet). */
static Value fire_s3d_HardwareError(OwnList* list) {
    const char* text = s3d_gpu_error();
    s3d_size len = strlen(text), i;
    Str* s = allocStr((uint32_t)len, list);
    for (i = 0; i < len; i++) strChars(s)[i] = (fire_char16)(uint8_t)text[i];
    return StrV(s);
}

#endif /* FIRE_BRIDGE_SCENE3D_H */
