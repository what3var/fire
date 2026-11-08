/* fire native bridge "scene3d" (C): the natives behind `#import "scene3d"` (docs/graphics/3d.md) - meshes, textures and the renderer that draws them into RGBA pixels. The fire side (Scene,
 * Camera, Object, Light, Texture, Material, Template, Model - fire source) is the same in the VM and in a native build; this file is what its `__S3D...` functions do.
 *
 * This file is the one implementation, in C99 ("language": "c"): the native build puts it into the generated program, and the virtual machine runs it in a shared library built from it
 * (native/abi/fire_pkg_abi.h). The renderer itself is s3d_engine.h (plain C; the software renderer) with its hardware backend s3d_gpu_vk.h (Vulkan, loaded when it is first asked for).
 * Meshes, textures and frames (the depth buffer of a camera) live here and are used by handle; the pixels are a byte buffer of the fire program (the camera copies it into its framebuffer
 * with `WriteBytes`). */
#ifndef FIRE_BRIDGE_SCENE3D_H
#define FIRE_BRIDGE_SCENE3D_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/* (a package puts its files into one source, the engine first: then it is there already) */
#ifndef FIRE_S3D_ENGINE_H
#include "s3d_engine.h"
#endif

/* ---- the tables: slot 0 is never used (handle 0 is "none"), a free slot is used again ---- */
typedef struct FireS3dTable { void** items; s3d_size count, cap; } FireS3dTable;
static FireS3dTable g_s3dMeshes, g_s3dTextures, g_s3dFrames;

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

static void fire_s3d_reset(void) {
    s3d_size i;
    for (i = 1; i < g_s3dFrames.count; i++) s3d_frameFree((S3dFrame*)g_s3dFrames.items[i]);
    for (i = 1; i < g_s3dMeshes.count; i++) s3d_meshFree((S3dMesh*)g_s3dMeshes.items[i]);
    for (i = 1; i < g_s3dTextures.count; i++) s3d_texFree((S3dTexture*)g_s3dTextures.items[i]);
    g_s3dFrames.count = g_s3dMeshes.count = g_s3dTextures.count = 0;
    s3d_gpu_shutdown();
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

/* The pitch and yaw (radians, an array of two floats) of a camera at (fx, fy, fz) that looks at (tx, ty, tz). */
static Value fire_s3d_LookAt(Value fx, Value fy, Value fz, Value tx, Value ty, Value tz, OwnList* list) {
    float rx, ry;
    Arr* a;
    s3d_lookAngles(s3d_v3(fire_s3d_F(fx), fire_s3d_F(fy), fire_s3d_F(fz)), s3d_v3(fire_s3d_F(tx), fire_s3d_F(ty), fire_s3d_F(tz)), &rx, &ry);
    a = allocArr(2, list);
    arrItems(a)[0] = Float((Real)rx);
    arrItems(a)[1] = Float((Real)ry);
    return ArrV(a);
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

/* min x, y, z and max x, y, z of the mesh (an array of six floats). */
static Value fire_s3d_MeshBounds(Value h, OwnList* list) {
    S3dMesh* m = (S3dMesh*)fire_s3d_find(&g_s3dMeshes, h);
    Arr* a = allocArr(6, list);
    float v[6] = {0, 0, 0, 0, 0, 0};
    int i;
    if (m) { v[0] = m->bmin.x; v[1] = m->bmin.y; v[2] = m->bmin.z; v[3] = m->bmax.x; v[4] = m->bmax.y; v[5] = m->bmax.z; }
    for (i = 0; i < 6; i++) arrItems(a)[i] = Float((Real)v[i]);
    return ArrV(a);
}

static Value fire_s3d_MeshTriangleCount(Value h) {
    S3dMesh* m = (S3dMesh*)fire_s3d_find(&g_s3dMeshes, h);
    return Int(m ? (int64_t)(m->idxN / 3) : 0);
}

static Value fire_s3d_texHandle(S3dTexture* t) {
    int64_t h;
    s3d_texPrepare(t);
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

static Value fire_s3d_TexFree(Value h) {
    S3dTexture* t = (S3dTexture*)fire_s3d_find(&g_s3dTextures, h);
    if (t) { s3d_texFree(t); g_s3dTextures.items[h.i] = NULL; }
    return Bool(true);
}

static Value fire_s3d_FrameNew(Value width, Value height) {
    S3dFrame* f;
    int64_t h;
    if (width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384) return Int(0);
    f = s3d_frameNew((int)width.i, (int)height.i);
    h = fire_s3d_add(&g_s3dFrames, f);
    if (h == 0) s3d_frameFree(f);
    return Int(h);
}

static Value fire_s3d_FrameFree(Value h) {
    S3dFrame* f = (S3dFrame*)fire_s3d_find(&g_s3dFrames, h);
    if (f) { s3d_frameFree(f); g_s3dFrames.items[h.i] = NULL; }
    return Bool(true);
}

/* Fills the pixels with `color` (0xAABBGGRR) and clears the depth buffer; `width`/`height` give the new size of the frame (the buffer must hold width * height * 4 bytes). */
static Value fire_s3d_FrameClear(Value h, Value buffer, Value width, Value height, Value color) {
    S3dFrame* f;
    Buf* b = bufferArg(buffer, "Scene3D.Camera.Render");
    if (b == NULL) return Undef();
    f = (S3dFrame*)fire_s3d_find(&g_s3dFrames, h);
    if (!f || width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384) return Bool(false);
    if ((s3d_size)b->length < (s3d_size)width.i * (s3d_size)height.i * 4) return Bool(false);
    if (f->w != width.i || f->h != height.i) { if (!s3d_frameResize(f, (int)width.i, (int)height.i)) return Bool(false); }
    else s3d_frameClearDepth(f);
    f->clearColor = (uint32_t)color.i;
    s3d_clearPixels(b->data, (int)width.i, (int)height.i, (uint32_t)color.i);
    return Bool(true);
}

/* The layout of the arrays: see Object.Parameters, Light.Parameters and Scene.Settings in the prelude. */
#define FIRE_S3D_OBJECT_PARAMS 36
#define FIRE_S3D_CAMERA_PARAMS 9
#define FIRE_S3D_LIGHT_PARAMS 14
#define FIRE_S3D_SETTING_PARAMS 3   /* (a fourth, the renderer, is optional) */

static S3dCamera fire_s3d_cameraOf(Arr* c) {
    S3dCamera cp;
    Value* v = arrItems(c);
    cp.pos = s3d_v3(fire_s3d_F(v[0]), fire_s3d_F(v[1]), fire_s3d_F(v[2]));
    cp.rx = fire_s3d_F(v[3]); cp.ry = fire_s3d_F(v[4]); cp.rz = fire_s3d_F(v[5]);
    cp.fovY = fire_s3d_F(v[6]); cp.nearPlane = fire_s3d_F(v[7]); cp.farPlane = fire_s3d_F(v[8]);
    return cp;
}

/* Queues a mesh for the frame (nothing is drawn until `Flush`): `object` has FIRE_S3D_OBJECT_PARAMS floats, `camera` FIRE_S3D_CAMERA_PARAMS. false on bad handles or arrays. */
static Value fire_s3d_Draw(Value frame, Value mesh, Value texture, Value object, Value camera) {
    S3dFrame* f = (S3dFrame*)fire_s3d_find(&g_s3dFrames, frame);
    S3dMesh* m = (S3dMesh*)fire_s3d_find(&g_s3dMeshes, mesh);
    S3dTexture* tex;
    Arr *o, *c;
    S3dObjectParams op;
    S3dSurface s;
    S3dCamera cam;
    float O[FIRE_S3D_OBJECT_PARAMS];
    int i;
    if (!f || !m || object.kind != K_Array || camera.kind != K_Array) return Bool(false);
    o = arrOf(object); c = arrOf(camera);
    if (o->length < (uint32_t)FIRE_S3D_OBJECT_PARAMS || c->length < (uint32_t)FIRE_S3D_CAMERA_PARAMS) return Bool(false);
    tex = texture.i > 0 ? (S3dTexture*)fire_s3d_find(&g_s3dTextures, texture) : NULL;
    for (i = 0; i < FIRE_S3D_OBJECT_PARAMS; i++) O[i] = fire_s3d_F(arrItems(o)[i]);
    op.pos = s3d_v3(O[0], O[1], O[2]); op.rx = O[3]; op.ry = O[4]; op.rz = O[5]; op.scale = s3d_v3(O[6], O[7], O[8]); op.pivot = s3d_v3(O[9], O[10], O[11]);
    s = s3d_surfaceDefault();
    s.color = s3d_v3(O[12], O[13], O[14]);
    s.specular = O[15]; s.shininess = O[16];
    s.smooth = O[17] != 0; s.twoSided = O[18] != 0;
    s.emissive = s3d_v3(O[19], O[20], O[21]);
    s.useTexture = O[22] != 0 && tex != NULL;
    {   /* the texture mapping: scale by the repeats, rotate, move: u' = c*ru*u - s*rv*v + ou ; v' = s*ru*u + c*rv*v + ov */
        float ru = O[23], rv = O[24], rot = O[25], ou = O[26], ov = O[27];
        float cs = cosf(rot), sn = sinf(rot);
        s.tm[0] = cs * ru; s.tm[1] = -sn * rv; s.tm[2] = ou;
        s.tm[3] = sn * ru; s.tm[4] = cs * rv; s.tm[5] = ov;
    }
    s.opacity = O[28]; s.cutoff = O[29];
    s.lit = O[30] != 0; s.castShadows = O[31] != 0; s.receiveShadows = O[32] != 0;
    s.layers = (int)O[33];
    s.bump = O[34];
    s.billboard = (int)O[35];
    cam = fire_s3d_cameraOf(c);
    s3d_queueMesh(f, &cam, m, &op, &s, tex);
    return Bool(true);
}

/* Draws what was queued into the pixels of the camera. `lights` has FIRE_S3D_LIGHT_PARAMS per light, `settings` FIRE_S3D_SETTING_PARAMS (shadows on/off, shadow map size, threads)
 * and optionally the renderer (0 software, 1 hardware: the GPU when there is one, else the software renderer). */
static Value fire_s3d_Flush(Value frame, Value buffer, Value camera, Value lights, Value settings) {
    S3dFrame* f;
    Arr *c, *l, *st;
    S3dLight* ls;
    s3d_size n = 0;
    uint32_t i;
    S3dFlushSettings fs;
    S3dCamera cam;
    Buf* b = bufferArg(buffer, "Scene3D.Camera.Render");
    if (b == NULL) return Undef();
    f = (S3dFrame*)fire_s3d_find(&g_s3dFrames, frame);
    if (!f || camera.kind != K_Array || lights.kind != K_Array || settings.kind != K_Array) return Bool(false);
    c = arrOf(camera); l = arrOf(lights); st = arrOf(settings);
    if (c->length < (uint32_t)FIRE_S3D_CAMERA_PARAMS || l->length % FIRE_S3D_LIGHT_PARAMS != 0 || st->length < (uint32_t)FIRE_S3D_SETTING_PARAMS) return Bool(false);
    if ((s3d_size)b->length < (s3d_size)f->w * (s3d_size)f->h * 4) return Bool(false);
    ls = (S3dLight*)S3D_MALLOC(sizeof(S3dLight) * (l->length / FIRE_S3D_LIGHT_PARAMS + 1));
    if (ls == NULL) return Bool(false);
    for (i = 0; i < l->length; i += FIRE_S3D_LIGHT_PARAMS) {
        const Value* L = arrItems(l) + i;
        S3dLight* light = &ls[n++];
        light->kind = (int)fire_s3d_F(L[0]);
        light->color = s3d_v3(fire_s3d_F(L[1]), fire_s3d_F(L[2]), fire_s3d_F(L[3]));
        light->intensity = fire_s3d_F(L[4]);
        light->pos = s3d_v3(fire_s3d_F(L[5]), fire_s3d_F(L[6]), fire_s3d_F(L[7]));
        light->dir = s3d_normalize(s3d_v3(fire_s3d_F(L[8]), fire_s3d_F(L[9]), fire_s3d_F(L[10])));
        light->range = fire_s3d_F(L[11]);
        light->castShadows = fire_s3d_F(L[12]) != 0;
        light->mask = (int)fire_s3d_F(L[13]);
    }
    fs.shadows = fire_s3d_F(arrItems(st)[0]) != 0;
    fs.shadowResolution = (int)fire_s3d_F(arrItems(st)[1]);
    fs.threads = (int)fire_s3d_F(arrItems(st)[2]);
    fs.backend = st->length > 3 && (int)fire_s3d_F(arrItems(st)[3]) == S3D_BACKEND_HARDWARE ? S3D_BACKEND_HARDWARE : S3D_BACKEND_SOFTWARE;
    cam = fire_s3d_cameraOf(c);
    s3d_flush(b->data, f, &cam, ls, n, &fs);
    S3D_FREE(ls);
    return Bool(true);
}

/* DirectDrawing: the frame's pictures go straight from the GPU into the windows that show the framebuffer `framebuffer` (on), or no longer (off). Returns whether it is attached
 * (false: no GPU, no swap chains, or no such framebuffer - the camera then works as without). */
static Value fire_s3d_FrameDirect(Value h, Value framebuffer, Value on) {
    S3dFrame* f = (S3dFrame*)fire_s3d_find(&g_s3dFrames, h);
    if (f == NULL) return Bool(false);
    if (on.i) return Bool(s3d_gpu_attach(f, framebuffer.i) != 0);
    s3d_gpu_detach(f);
    return Bool(false);
}

/* Who drew the last picture of the frame: 0 the software renderer, 1 the GPU (the picture is in the pixels), 2 the GPU straight into the windows (DirectDrawing: not in the pixels). */
static Value fire_s3d_FrameRenderer(Value h) {
    S3dFrame* f = (S3dFrame*)fire_s3d_find(&g_s3dFrames, h);
    return Int(f ? f->lastBackend : 0);
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
