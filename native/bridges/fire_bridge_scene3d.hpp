// fire native bridge "scene3d": the natives behind `#import "scene3d"` (docs/SCENE3D.md) - meshes, textures and the software renderer that draws them into RGBA pixels. The fire side (Scene,
// Camera, Object, Light, Texture, Material, Template, Model - fire source) is the same in the VM and in a native build; this file is what its `__S3D...` functions do.
//
// This file is the one implementation: the native build includes it (the package "scene3d" brings it as its C++ source) and the virtual machine runs it in a shared library built from it
// (native/abi/fire_pkg_abi.h). The renderer itself is scene3d/s3d_engine.hpp (plain C++). Meshes, textures and frames (the depth buffer of a camera) live here and are used by handle; the
// pixels are a byte buffer of the fire program (the camera copies it into its framebuffer with `WriteBytes`).
#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

// (a package puts its files into one source, the engine first: then it is there already)
#ifndef FIRE_S3D_ENGINE_HPP
#include "scene3d/s3d_engine.hpp"
#endif

namespace fire {
namespace s3d {

struct Tables {
    std::vector<std::unique_ptr<Mesh>> meshes{1};
    std::vector<std::unique_ptr<Texture>> textures{1};
    std::vector<std::unique_ptr<Frame>> frames{1};
};
inline Tables& tables() { static Tables t; return t; }

inline void reset() {
    Tables& t = tables();
    t.meshes.clear();
    t.meshes.resize(1);
    t.textures.clear();
    t.textures.resize(1);
    t.frames.clear();
    t.frames.resize(1);
}

template <class T>
inline T* find(std::vector<std::unique_ptr<T>>& v, Value h) {
    int64_t i = h.i;
    if (i <= 0 || (size_t)i >= v.size() || !v[(size_t)i]) return nullptr;
    return v[(size_t)i].get();
}

template <class T>
inline int64_t add(std::vector<std::unique_ptr<T>>& v, T* item) {
    // a free slot is used again
    for (size_t i = 1; i < v.size(); i++)
        if (!v[i]) { v[i].reset(item); return (int64_t)i; }
    v.emplace_back(item);
    return (int64_t)v.size() - 1;
}

inline float F(Value v) { return (float)toR(v); }

/// Mesh from three arrays: positions (x, y, z per vertex), texture coordinates (u, v per vertex; may be empty) and indices (three per triangle). Returns the handle, 0 on bad data.
inline Value MeshNew(Value positions, Value uvs, Value indices) {
    if (positions.kind != K_Array || uvs.kind != K_Array || indices.kind != K_Array) return Int(0);
    Arr* p = arrOf(positions);
    Arr* t = arrOf(uvs);
    Arr* ix = arrOf(indices);
    if (p->length == 0 || p->length % 3 != 0 || ix->length == 0 || ix->length % 3 != 0) return Int(0);
    uint32_t vertices = p->length / 3;
    if (t->length != 0 && t->length != vertices * 2) return Int(0);
    auto mesh = new Mesh();
    mesh->pos.resize(p->length);
    for (uint32_t i = 0; i < p->length; i++) mesh->pos[i] = F(p->items()[i]);
    mesh->uv.assign(vertices * 2, 0.0f);
    for (uint32_t i = 0; i < t->length; i++) mesh->uv[i] = F(t->items()[i]);
    mesh->idx.resize(ix->length);
    for (uint32_t i = 0; i < ix->length; i++) {
        int64_t k = ix->items()[i].i;
        if (k < 0 || (uint64_t)k >= vertices) { delete mesh; return Int(0); }
        mesh->idx[i] = (uint32_t)k;
    }
    mesh->finish();
    return Int(add(tables().meshes, mesh));
}

/// A template mesh: 0 box, 1 sphere, 2 cylinder, 3 cone, 4 plane, 5 quad; `detail` is the number of segments around.
inline Value MeshTemplate(Value kind, Value detail) {
    if (kind.i < 0 || kind.i > 5) return Int(0);
    return Int(add(tables().meshes, makeTemplate((int)kind.i, (int)detail.i)));
}

/// A mesh from the text of a Wavefront OBJ file; 0 when there is nothing usable in it.
inline Value MeshFromObj(Value text) {
    if (text.kind != K_String) return Int(0);
    const Str* s = strOf(text);
    std::string ascii;
    ascii.reserve(s->length);
    for (uint32_t i = 0; i < s->length; i++) ascii.push_back(s->data[i] < 128 ? (char)s->data[i] : '?');
    Mesh* m = parseObj(ascii);
    return Int(m ? add(tables().meshes, m) : 0);
}

/// The pitch and yaw (radians, an array of two floats) of a camera at (fx, fy, fz) that looks at (tx, ty, tz).
inline Value LookAt(Value fx, Value fy, Value fz, Value tx, Value ty, Value tz, OwnList* list) {
    float rx, ry;
    lookAngles(Vec3{F(fx), F(fy), F(fz)}, Vec3{F(tx), F(ty), F(tz)}, rx, ry);
    Arr* a = allocArr(2, list);
    a->items()[0] = Float((Real)rx);
    a->items()[1] = Float((Real)ry);
    return ArrV(a);
}

/// Another user of the mesh (an object that shares the mesh of a model): the mesh lives until every user has released it.
inline Value MeshRetain(Value h) {
    Mesh* m = find(tables().meshes, h);
    if (m) m->refs++;
    return Bool(m != nullptr);
}

inline Value MeshFree(Value h) {
    Tables& t = tables();
    int64_t i = h.i;
    if (i > 0 && (size_t)i < t.meshes.size() && t.meshes[(size_t)i] && --t.meshes[(size_t)i]->refs <= 0) t.meshes[(size_t)i].reset();
    return Bool(true);
}

/// min x, y, z and max x, y, z of the mesh (an array of six floats).
inline Value MeshBounds(Value h, OwnList* list) {
    Mesh* m = find(tables().meshes, h);
    Arr* a = allocArr(6, list);
    float v[6] = {0, 0, 0, 0, 0, 0};
    if (m) { v[0] = m->bmin.x; v[1] = m->bmin.y; v[2] = m->bmin.z; v[3] = m->bmax.x; v[4] = m->bmax.y; v[5] = m->bmax.z; }
    for (int i = 0; i < 6; i++) a->items()[i] = Float((Real)v[i]);
    return ArrV(a);
}

inline Value MeshTriangleCount(Value h) {
    Mesh* m = find(tables().meshes, h);
    return Int(m ? (int64_t)(m->idx.size() / 3) : 0);
}

/// A texture from `width * height * 4` bytes (R, G, B, A); copied. Returns the handle, 0 on bad data.
inline Value TexNew(Value width, Value height, Value buffer) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    if (width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384) return Int(0);
    size_t n = (size_t)width.i * (size_t)height.i * 4;
    if ((size_t)bufOf(buffer)->length < n) return Int(0);
    auto tex = new Texture();
    tex->w = (int)width.i; tex->h = (int)height.i;
    tex->rgba.assign(bufOf(buffer)->bytes(), bufOf(buffer)->bytes() + n);
    tex->prepare();
    return Int(add(tables().textures, tex));
}

/// A checker board of `cells` x `cells` squares in two colours (0xAABBGGRR), in a picture of `size` x `size` pixels.
inline Value TexChecker(Value size, Value cells, Value colorA, Value colorB) {
    if (size.i <= 0 || size.i > 4096 || cells.i <= 0 || cells.i > size.i) return Int(0);
    auto tex = new Texture();
    int n = (int)size.i;
    tex->w = tex->h = n;
    tex->rgba.resize((size_t)n * (size_t)n * 4);
    for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++) {
            uint32_t c = ((x * cells.i / n + y * cells.i / n) & 1) ? (uint32_t)colorB.i : (uint32_t)colorA.i;
            uint8_t* p = &tex->rgba[((size_t)y * (size_t)n + (size_t)x) * 4];
            p[0] = c & 255; p[1] = (c >> 8) & 255; p[2] = (c >> 16) & 255; p[3] = (c >> 24) & 255;
        }
    tex->prepare();
    return Int(add(tables().textures, tex));
}

/// Gives a texture a bump map made from its own brightness.
inline Value TexBumpAuto(Value h) {
    Texture* t = find(tables().textures, h);
    if (!t) return Bool(false);
    t->makeBumpFromColors();
    return Bool(true);
}

/// Gives a texture a bump map from a picture: `channels` bytes per pixel (1: heights; 3 or 4: colours, whose brightness is the height); the picture is scaled to the texture.
inline Value TexBump(Value h, Value width, Value height, Value channels, Value buffer) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Texture* t = find(tables().textures, h);
    if (!t || width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384 || (channels.i != 1 && channels.i != 3 && channels.i != 4)) return Bool(false);
    if ((size_t)bufOf(buffer)->length < (size_t)width.i * (size_t)height.i * (size_t)channels.i) return Bool(false);
    t->setBump(bufOf(buffer)->bytes(), (int)width.i, (int)height.i, (int)channels.i);
    return Bool(true);
}

/// Takes the bump map away again.
inline Value TexBumpClear(Value h) {
    Texture* t = find(tables().textures, h);
    if (!t) return Bool(false);
    t->bump.clear();
    return Bool(true);
}

inline Value TexFree(Value h) {
    Tables& t = tables();
    int64_t i = h.i;
    if (i > 0 && (size_t)i < t.textures.size()) t.textures[(size_t)i].reset();
    return Bool(true);
}

inline Value FrameNew(Value width, Value height) {
    if (width.i <= 0 || height.i <= 0 || width.i > 16384 || height.i > 16384) return Int(0);
    auto f = new Frame();
    f->resize((int)width.i, (int)height.i);
    return Int(add(tables().frames, f));
}

inline Value FrameFree(Value h) {
    Tables& t = tables();
    int64_t i = h.i;
    if (i > 0 && (size_t)i < t.frames.size()) t.frames[(size_t)i].reset();
    return Bool(true);
}

/// Fills the pixels with `color` (0xAABBGGRR) and clears the depth buffer; `width`/`height` give the new size of the frame (the buffer must hold width * height * 4 bytes).
inline Value FrameClear(Value h, Value buffer, Value width, Value height, Value color) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Frame* f = find(tables().frames, h);
    if (!f || width.i <= 0 || height.i <= 0) return Bool(false);
    if ((size_t)bufOf(buffer)->length < (size_t)width.i * (size_t)height.i * 4) return Bool(false);
    if (f->w != width.i || f->h != height.i) f->resize((int)width.i, (int)height.i); else f->clearDepth();
    clearPixels(bufOf(buffer)->bytes(), (int)width.i, (int)height.i, (uint32_t)color.i);
    return Bool(true);
}

// The layout of the arrays: see Object.Parameters, Light.Parameters and Scene.Settings in the prelude.
constexpr int kObjectParams = 36;
constexpr int kCameraParams = 9;
constexpr int kLightParams = 14;
constexpr int kSettingParams = 3;

inline CameraParams cameraOf(Arr* c) {
    CameraParams cp;
    cp.pos = {F(c->items()[0]), F(c->items()[1]), F(c->items()[2])};
    cp.rx = F(c->items()[3]); cp.ry = F(c->items()[4]); cp.rz = F(c->items()[5]);
    cp.fovY = F(c->items()[6]); cp.nearPlane = F(c->items()[7]); cp.farPlane = F(c->items()[8]);
    return cp;
}

/// Queues a mesh for the frame (nothing is drawn until `Flush`): `object` has kObjectParams floats, `camera` kCameraParams. false on bad handles or arrays.
inline Value Draw(Value frame, Value mesh, Value texture, Value object, Value camera) {
    Frame* f = find(tables().frames, frame);
    Mesh* m = find(tables().meshes, mesh);
    if (!f || !m || object.kind != K_Array || camera.kind != K_Array) return Bool(false);
    Arr* o = arrOf(object);
    Arr* c = arrOf(camera);
    if (o->length < (uint32_t)kObjectParams || c->length < (uint32_t)kCameraParams) return Bool(false);
    Texture* tex = texture.i > 0 ? find(tables().textures, texture) : nullptr;
    auto O = [&](int i) { return F(o->items()[i]); };
    ObjectParams op;
    op.pos = {O(0), O(1), O(2)}; op.rx = O(3); op.ry = O(4); op.rz = O(5); op.scale = {O(6), O(7), O(8)}; op.pivot = {O(9), O(10), O(11)};
    Surface s;
    s.color = {O(12), O(13), O(14)};
    s.specular = O(15); s.shininess = O(16);
    s.smooth = O(17) != 0; s.twoSided = O(18) != 0;
    s.emissive = {O(19), O(20), O(21)};
    s.useTexture = O(22) != 0 && tex != nullptr;
    {   // the texture mapping: scale by the repeats, rotate, move: u' = c*ru*u - s*rv*v + ou ; v' = s*ru*u + c*rv*v + ov
        float ru = O(23), rv = O(24), rot = O(25), ou = O(26), ov = O(27);
        float cs = std::cos(rot), sn = std::sin(rot);
        s.tm[0] = cs * ru; s.tm[1] = -sn * rv; s.tm[2] = ou;
        s.tm[3] = sn * ru; s.tm[4] = cs * rv; s.tm[5] = ov;
    }
    s.opacity = O(28); s.cutoff = O(29);
    s.lit = O(30) != 0; s.castShadows = O(31) != 0; s.receiveShadows = O(32) != 0;
    s.layers = (int)O(33);
    s.bump = O(34);
    s.billboard = (int)O(35);
    queueMesh(*f, cameraOf(arrOf(camera)), *m, op, s, tex);
    return Bool(true);
}

/// Rasterises what was queued into the pixels of the camera. `lights` has kLightParams per light, `settings` kSettingParams (shadows on/off, shadow map size, threads).
inline Value Flush(Value frame, Value buffer, Value camera, Value lights, Value settings) {
    if (!leafAlive(buffer)) return destroyedError(buffer);
    Frame* f = find(tables().frames, frame);
    if (!f || camera.kind != K_Array || lights.kind != K_Array || settings.kind != K_Array) return Bool(false);
    Arr* c = arrOf(camera);
    Arr* l = arrOf(lights);
    Arr* st = arrOf(settings);
    if (c->length < (uint32_t)kCameraParams || l->length % kLightParams != 0 || st->length < (uint32_t)kSettingParams) return Bool(false);
    if ((size_t)bufOf(buffer)->length < (size_t)f->w * (size_t)f->h * 4) return Bool(false);
    std::vector<Light> ls;
    for (uint32_t i = 0; i < l->length; i += kLightParams) {
        auto L = [&](int k) { return F(l->items()[i + k]); };
        Light light;
        light.kind = (int)L(0);
        light.color = {L(1), L(2), L(3)};
        light.intensity = L(4);
        light.pos = {L(5), L(6), L(7)};
        light.dir = normalize(Vec3{L(8), L(9), L(10)});
        light.range = L(11);
        light.castShadows = L(12) != 0;
        light.mask = (int)L(13);
        ls.push_back(light);
    }
    FlushSettings fs;
    fs.shadows = F(st->items()[0]) != 0;
    fs.shadowResolution = (int)F(st->items()[1]);
    fs.threads = (int)F(st->items()[2]);
    flush(bufOf(buffer)->bytes(), *f, cameraOf(c), ls, fs);
    return Bool(true);
}

}  // namespace s3d
}  // namespace fire
