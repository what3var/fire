// fire scene3d: the software renderer behind `#import "scene3d"` (docs/SCENE3D.md). Plain C++ that knows nothing of fire: triangles go in, RGBA pixels come out.
//
// What it does: a perspective camera (vertical field of view), the model matrix of an object from its position, rotation, scale and pivot, clipping against the near plane, back-face
// culling, a depth buffer (1/w interpolation of z in screen space), perspective-correct interpolation of the texture coordinates, the normal and the world position, textures (repeat,
// bilinear), and Blinn-Phong lighting with an ambient, directional and point lights per pixel. A mesh with smooth normals is shaded with the interpolated vertex normal, a flat one with the
// normal of its face.
//
// Drawing is queue-then-flush: `queueMesh` transforms an object and sets up its triangles, `flush` rasterises the queue (opaque objects first, then the transparent ones from far to near) in
// bands of rows that are shared between threads. Also: transparency (opacity, textures with alpha, cut-outs), billboards, shadow maps (directional and point lights, soft edge), bump mapping,
// lights and surfaces that can be switched off or limited to layers.
//
// One implementation for everything: the native build includes it and the virtual machine runs it in a shared library (native/abi/fire_pkg_abi.h), so a picture is the same in both.
// Right handed, Y up; a camera without rotation looks along -Z; angles in radians, lengths in meters (the fire side converts the units).
#pragma once
#ifndef FIRE_S3D_ENGINE_HPP
#define FIRE_S3D_ENGINE_HPP

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#if !defined(FIRE_S3D_NO_THREADS) && !defined(__EMSCRIPTEN__) && !defined(ESP_PLATFORM)
#include <thread>
#endif
#include <utility>
#include <vector>

namespace fire {
namespace s3d {

struct Vec3 { float x = 0, y = 0, z = 0; };
inline Vec3 operator+(Vec3 a, Vec3 b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
inline Vec3 operator-(Vec3 a, Vec3 b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
inline Vec3 operator*(Vec3 a, float s) { return {a.x * s, a.y * s, a.z * s}; }
inline float dot(Vec3 a, Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline Vec3 cross(Vec3 a, Vec3 b) { return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x}; }
inline float length(Vec3 a) { return std::sqrt(dot(a, a)); }
inline Vec3 normalize(Vec3 a) { float l = length(a); return l > 1e-20f ? a * (1.0f / l) : Vec3{0, 1, 0}; }

/// A 4x4 matrix, row major, applied to column vectors: v' = M * v.
struct Mat4 {
    float m[16];
    static Mat4 identity() { Mat4 r; for (int i = 0; i < 16; i++) r.m[i] = (i % 5 == 0) ? 1.0f : 0.0f; return r; }
    float& at(int row, int col) { return m[row * 4 + col]; }
    float at(int row, int col) const { return m[row * 4 + col]; }
};
inline Mat4 mul(const Mat4& a, const Mat4& b) {
    Mat4 r;
    for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++) {
            float s = 0;
            for (int k = 0; k < 4; k++) s += a.at(i, k) * b.at(k, j);
            r.at(i, j) = s;
        }
    return r;
}
inline Mat4 translation(Vec3 t) { Mat4 r = Mat4::identity(); r.at(0, 3) = t.x; r.at(1, 3) = t.y; r.at(2, 3) = t.z; return r; }
inline Mat4 scaling(Vec3 s) { Mat4 r = Mat4::identity(); r.at(0, 0) = s.x; r.at(1, 1) = s.y; r.at(2, 2) = s.z; return r; }
inline Mat4 rotationX(float a) { Mat4 r = Mat4::identity(); float c = std::cos(a), s = std::sin(a); r.at(1, 1) = c; r.at(1, 2) = -s; r.at(2, 1) = s; r.at(2, 2) = c; return r; }
inline Mat4 rotationY(float a) { Mat4 r = Mat4::identity(); float c = std::cos(a), s = std::sin(a); r.at(0, 0) = c; r.at(0, 2) = s; r.at(2, 0) = -s; r.at(2, 2) = c; return r; }
inline Mat4 rotationZ(float a) { Mat4 r = Mat4::identity(); float c = std::cos(a), s = std::sin(a); r.at(0, 0) = c; r.at(0, 1) = -s; r.at(1, 0) = s; r.at(1, 1) = c; return r; }
/// The rotation of an object: first around Z (roll), then X (pitch), then Y (yaw).
inline Mat4 rotationXYZ(float rx, float ry, float rz) { return mul(rotationY(ry), mul(rotationX(rx), rotationZ(rz))); }
inline Vec3 transformPoint(const Mat4& m, Vec3 p) {
    return {m.at(0, 0) * p.x + m.at(0, 1) * p.y + m.at(0, 2) * p.z + m.at(0, 3), m.at(1, 0) * p.x + m.at(1, 1) * p.y + m.at(1, 2) * p.z + m.at(1, 3), m.at(2, 0) * p.x + m.at(2, 1) * p.y + m.at(2, 2) * p.z + m.at(2, 3)};
}
inline Vec3 transformDir(const Mat4& m, Vec3 p) {
    return {m.at(0, 0) * p.x + m.at(0, 1) * p.y + m.at(0, 2) * p.z, m.at(1, 0) * p.x + m.at(1, 1) * p.y + m.at(1, 2) * p.z, m.at(2, 0) * p.x + m.at(2, 1) * p.y + m.at(2, 2) * p.z};
}

/// A triangle mesh: positions, texture coordinates, and the vertex normals (computed here as the area weighted average of the faces around a vertex).
struct Mesh {
    std::vector<float> pos;        // 3 per vertex
    std::vector<float> uv;         // 2 per vertex
    std::vector<float> nrm;        // 3 per vertex
    std::vector<uint32_t> idx;     // 3 per triangle
    Vec3 bmin, bmax;
    int refs = 1;                  // the users of the mesh (see MeshFree)

    void finish() {
        size_t n = pos.size() / 3;
        uv.resize(n * 2, 0.0f);
        nrm.assign(n * 3, 0.0f);
        if (n == 0) { bmin = bmax = Vec3{}; return; }
        bmin = bmax = Vec3{pos[0], pos[1], pos[2]};
        for (size_t i = 0; i < n; i++) {
            bmin = {std::min(bmin.x, pos[i * 3]), std::min(bmin.y, pos[i * 3 + 1]), std::min(bmin.z, pos[i * 3 + 2])};
            bmax = {std::max(bmax.x, pos[i * 3]), std::max(bmax.y, pos[i * 3 + 1]), std::max(bmax.z, pos[i * 3 + 2])};
        }
        for (size_t t = 0; t + 2 < idx.size(); t += 3) {
            uint32_t a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if (a >= n || b >= n || c >= n) continue;
            Vec3 pa{pos[a * 3], pos[a * 3 + 1], pos[a * 3 + 2]}, pb{pos[b * 3], pos[b * 3 + 1], pos[b * 3 + 2]}, pc{pos[c * 3], pos[c * 3 + 1], pos[c * 3 + 2]};
            Vec3 fn = cross(pb - pa, pc - pa);   // its length is twice the area: the weight
            for (uint32_t v : {a, b, c}) { nrm[v * 3] += fn.x; nrm[v * 3 + 1] += fn.y; nrm[v * 3 + 2] += fn.z; }
        }
        for (size_t i = 0; i < n; i++) {
            Vec3 v = normalize(Vec3{nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]});
            nrm[i * 3] = v.x; nrm[i * 3 + 1] = v.y; nrm[i * 3 + 2] = v.z;
        }
    }
};

struct Texture {
    int w = 0, h = 0;
    std::vector<uint8_t> rgba;
    std::vector<uint8_t> bump;   // a height (0..255) per texel, for bump mapping; empty: none
    bool hasAlpha = false;       // some texel is not opaque (see prepare)

    void prepare() {
        hasAlpha = false;
        for (size_t i = 3; i < rgba.size(); i += 4) if (rgba[i] != 255) { hasAlpha = true; break; }
    }

    /// The heights from the brightness of the texture itself (a little smoothed: a sharp edge gives a bevel, not a one texel crack).
    void makeBumpFromColors() {
        std::vector<float> lum((size_t)w * (size_t)h);
        for (size_t i = 0; i < lum.size(); i++) lum[i] = 0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2];
        bump.resize(lum.size());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                float sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++) sum += lum[(size_t)((y + dy + h) % h) * (size_t)w + (size_t)((x + dx + w) % w)] * ((dx == 0 && dy == 0) ? 4.0f : (dx == 0 || dy == 0) ? 2.0f : 1.0f);
                bump[(size_t)y * (size_t)w + (size_t)x] = (uint8_t)std::min(255.0f, sum / 16.0f + 0.5f);
            }
    }

    /// The heights from a picture of another size: `data` holds `channels` bytes per pixel (1: heights, 3 or 4: colours, whose brightness is the height); it is scaled to this texture.
    void setBump(const uint8_t* data, int dw, int dh, int channels) {
        bump.resize((size_t)w * (size_t)h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int sx = std::min(dw - 1, (int)((int64_t)x * dw / w)), sy = std::min(dh - 1, (int)((int64_t)y * dh / h));
                const uint8_t* p = data + ((size_t)sy * (size_t)dw + (size_t)sx) * (size_t)channels;
                float v = channels == 1 ? (float)p[0] : 0.299f * p[0] + 0.587f * p[1] + 0.114f * p[2];
                bump[(size_t)y * (size_t)w + (size_t)x] = (uint8_t)std::min(255.0f, v + 0.5f);
            }
    }
};

enum LightKind { LightAmbient = 0, LightDirectional = 1, LightPoint = 2 };
struct Light {
    int kind = LightAmbient;
    Vec3 color{1, 1, 1};
    float intensity = 1;
    Vec3 pos;          // point
    Vec3 dir{0, 0, -1};// directional: the direction the light travels
    float range = 10;  // point: the distance at which it has half its strength
    bool castShadows = false;
    int mask = -1;     // the layers of objects that it lights (an object has its layers, too: they have to share one)
};

struct CameraParams {
    Vec3 pos;
    float rx = 0, ry = 0, rz = 0;
    float fovY = 1.0471976f;   // 60 degrees
    float nearPlane = 0.1f, farPlane = 500.0f;
};

/// What a material and the object's look decide: the base colour, the texture mapping (2x3 matrix on the texture coordinates), the surface.
struct Surface {
    Vec3 color{0.8f, 0.8f, 0.8f};
    float specular = 0, shininess = 32;
    bool smooth = true, twoSided = false;
    Vec3 emissive;
    bool useTexture = false;
    float tm[6] = {1, 0, 0, 0, 1, 0};   // u' = a*u + b*v + tx ; v' = c*u + d*v + ty  stored as {a, b, tx, c, d, ty}
    float opacity = 1;                  // below 1 (or a texture with transparent texels): blended with what is behind
    float cutoff = 0;                   // > 0: a texel with a smaller alpha is not drawn at all (a cut-out: leaves, fences), and the rest is opaque
    bool lit = true;                    // false: no lighting, the colour as it is (and the emissive part)
    bool castShadows = true, receiveShadows = true;
    int layers = -1;
    float bump = 0;                     // the strength of the bump map of the texture (0: none)
    int billboard = 0;                  // 0 none, 1 faces the camera, 2 turns around the vertical axis only
};

struct ObjectParams {
    Vec3 pos, scale{1, 1, 1}, pivot;   // the pivot is a point of the mesh (in mesh space) that sits at `pos`
    float rx = 0, ry = 0, rz = 0;
};

inline void clearPixels(uint8_t* rgba, int w, int h, uint32_t color) {
    uint8_t r = color & 255, g = (color >> 8) & 255, b = (color >> 16) & 255, a = (color >> 24) & 255;
    uint32_t packed;
    uint8_t bytes[4] = {r, g, b, a};
    std::memcpy(&packed, bytes, 4);
    uint32_t* p = reinterpret_cast<uint32_t*>(rgba);
    for (size_t i = 0, n = (size_t)w * (size_t)h; i < n; i++) p[i] = packed;
}

/// A vertex while it is clipped and rasterised: clip space position and the attributes that are interpolated.
struct ClipVertex {
    float x, y, z, w;
    float wx, wy, wz;   // world position
    float nx, ny, nz;   // world normal
    float u, v;
};

inline ClipVertex lerpVertex(const ClipVertex& a, const ClipVertex& b, float t) {
    ClipVertex r;
    auto L = [t](float p, float q) { return p + (q - p) * t; };
    r.x = L(a.x, b.x); r.y = L(a.y, b.y); r.z = L(a.z, b.z); r.w = L(a.w, b.w);
    r.wx = L(a.wx, b.wx); r.wy = L(a.wy, b.wy); r.wz = L(a.wz, b.wz);
    r.nx = L(a.nx, b.nx); r.ny = L(a.ny, b.ny); r.nz = L(a.nz, b.nz);
    r.u = L(a.u, b.u); r.v = L(a.v, b.v);
    return r;
}

inline int floorInt(float x) { int i = (int)x; return i - (x < (float)i ? 1 : 0); }

// ---------------------------------------------------------------------------------------------------------------------
// The frame: everything a camera draws is first QUEUED (the objects are transformed and cut to the screen, their triangles set up) and then rasterised in one go by `flush`: the rows of the
// picture are shared between threads, every pixel belongs to one of them and gets its triangles in the order they were queued, so the picture does not depend on the number of threads.
// ---------------------------------------------------------------------------------------------------------------------
constexpr int kAttrs = 10;   // z, 1/w, u/w, v/w, normal/w (3), position/w (3)

/// One object drawn into the frame: what its triangles share.
struct Batch {
    Surface s;
    const Texture* tex = nullptr;
    const Mesh* mesh = nullptr;
    std::vector<ClipVertex> cv;   // the vertices in world space, with the transformed texture coordinates and the clip space position
    float sortKey = 0;            // the distance to the camera: blended objects are drawn from far to near
    bool blended = false;
};

/// A triangle ready to be filled: its place on the screen and the planes of the attributes (value at vertex 0 and the change per pixel in x and y).
struct Tri {
    int batch = 0;
    int minY = 0, maxY = -1;
    float sx[3], sy[3];
    float base[kAttrs], gx[kAttrs], gy[kAttrs];
    Vec3 fn;                 // the face normal (world)
    bool constNormal = false;
    bool bump = false;
    Vec3 tangent, bitangent; // world directions of increasing u and v (for bump mapping)
};

struct ShadowMap {
    int res = 0;
    bool ortho = false;
    Mat4 view;               // world -> light view (looking down -z)
    float sx = 1, sy = 1, ox = 0, oy = 0;   // ortho: u = x*sx + ox ; v = y*sy + oy
    float f = 1;             // perspective: u = x/d*f*0.5 + 0.5
    std::vector<float> depth;
};

struct ShadowSet {
    int count = 0;           // 1 for a directional light, 6 faces for a point light
    ShadowMap maps[6];
};

struct Frame {
    int w = 0, h = 0;
    std::vector<float> depth;
    std::vector<Batch> batches;
    std::vector<Tri> opaque, blended;
    void resize(int nw, int nh) { w = nw; h = nh; depth.assign((size_t)nw * (size_t)nh, 1e30f); batches.clear(); opaque.clear(); blended.clear(); }
    void clearDepth() { std::fill(depth.begin(), depth.end(), 1e30f); batches.clear(); opaque.clear(); blended.clear(); }
};

struct FlushSettings {
    bool shadows = true;      // false: no shadow maps at all
    int shadowResolution = 1024;
    int threads = 0;          // 0: as many as the machine has (up to 8), 1: none
};

/// The lighting of one point: `color` is the base colour (surface times texture), `n` the unit normal towards the viewer's side, `p` the position, `eye` the camera. Lights come prepared.
struct PreparedLight {
    int kind;
    Vec3 color;       // colour times intensity
    Vec3 pos;
    Vec3 toLight;     // directional: the unit direction towards the light
    float invRange;
    int mask;
    int shadow;       // index into the shadow sets, -1: casts none
};

struct Context {
    Vec3 eye;
    Vec3 ambient;
    std::vector<PreparedLight> lights;
    std::vector<ShadowSet> shadows;
};

inline bool shadowProject(const ShadowMap& m, Vec3 p, float& u, float& v, float& d) {
    Vec3 q = transformPoint(m.view, p);
    d = -q.z;
    if (m.ortho) { u = q.x * m.sx + m.ox; v = 1.0f - (q.y * m.sy + m.oy); return true; }
    if (d <= 1e-6f) return false;
    float k = m.f * 0.5f / d;
    u = q.x * k + 0.5f; v = 1.0f - (q.y * k + 0.5f);
    return true;
}

/// How much of the light reaches `p` (0 = in the shadow, 1 = lit): the depth in the shadow map of the light, compared at the four texels around the point and weighted bilinearly (a soft edge).
inline float shadowFactor(const ShadowSet& set, Vec3 lightPos, bool directional, Vec3 p, Vec3 n) {
    const ShadowMap* map = &set.maps[0];
    if (!directional) {
        Vec3 d = p - lightPos;
        float ax = std::fabs(d.x), ay = std::fabs(d.y), az = std::fabs(d.z);
        int face = ax >= ay && ax >= az ? (d.x >= 0 ? 0 : 1) : ay >= az ? (d.y >= 0 ? 2 : 3) : (d.z >= 0 ? 4 : 5);
        map = &set.maps[face];
    }
    float u, v, depth, texel;
    if (map->ortho) texel = 1.0f / (map->sx * (float)map->res);
    else {
        if (!shadowProject(*map, p, u, v, depth)) return 1.0f;
        texel = 2.0f * depth / (map->f * (float)map->res);
    }
    // move the point along its normal by about a texel: a surface does not shadow itself
    Vec3 q = p + n * (texel * 1.5f);
    if (!shadowProject(*map, q, u, v, depth)) return 1.0f;
    float bias = texel * 1.0f + 0.002f;
    if (u < 0 || v < 0 || u >= 1 || v >= 1) return 1.0f;
    float fx = u * (float)map->res - 0.5f, fy = v * (float)map->res - 0.5f;
    int x0 = floorInt(fx), y0 = floorInt(fy);
    float tx = fx - (float)x0, ty = fy - (float)y0;
    int last = map->res - 1;
    int x1 = std::min(last, x0 + 1), y1 = std::min(last, y0 + 1);
    x0 = std::max(0, x0); y0 = std::max(0, y0);
    const float* d = map->depth.data();
    size_t R = (size_t)map->res;
    float limit = depth - bias;
    float s00 = limit <= d[(size_t)y0 * R + (size_t)x0] ? 1.0f : 0.0f, s10 = limit <= d[(size_t)y0 * R + (size_t)x1] ? 1.0f : 0.0f;
    float s01 = limit <= d[(size_t)y1 * R + (size_t)x0] ? 1.0f : 0.0f, s11 = limit <= d[(size_t)y1 * R + (size_t)x1] ? 1.0f : 0.0f;
    return (s00 * (1 - tx) + s10 * tx) * (1 - ty) + (s01 * (1 - tx) + s11 * tx) * ty;
}

/// Texture coordinates in repeat mode, bilinear; v = 0 is the top row of the picture. `alpha` is the transparency of the point (0..1).
inline Vec3 sampleTexture(const Texture& t, float u, float v, float& alpha) {
    float x = (u - (float)floorInt(u)) * (float)t.w - 0.5f, y = (v - (float)floorInt(v)) * (float)t.h - 0.5f;
    int x0 = floorInt(x), y0 = floorInt(y);
    float tx = x - (float)x0, ty = y - (float)y0;
    int x1 = x0 + 1, y1 = y0 + 1;
    if (x0 < 0) x0 += t.w;
    if (y0 < 0) y0 += t.h;
    if (x1 >= t.w) x1 -= t.w;
    if (y1 >= t.h) y1 -= t.h;
    if (x0 >= t.w) x0 = t.w - 1;
    if (y0 >= t.h) y0 = t.h - 1;
    const uint8_t* p00 = &t.rgba[((size_t)y0 * (size_t)t.w + (size_t)x0) * 4];
    const uint8_t* p10 = &t.rgba[((size_t)y0 * (size_t)t.w + (size_t)x1) * 4];
    const uint8_t* p01 = &t.rgba[((size_t)y1 * (size_t)t.w + (size_t)x0) * 4];
    const uint8_t* p11 = &t.rgba[((size_t)y1 * (size_t)t.w + (size_t)x1) * 4];
    float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
    const float k = 1.0f / 255.0f;
    alpha = (p00[3] * w00 + p10[3] * w10 + p01[3] * w01 + p11[3] * w11) * k;
    return {(p00[0] * w00 + p10[0] * w10 + p01[0] * w01 + p11[0] * w11) * k, (p00[1] * w00 + p10[1] * w10 + p01[1] * w01 + p11[1] * w11) * k, (p00[2] * w00 + p10[2] * w10 + p01[2] * w01 + p11[2] * w11) * k};
}

inline float sampleBump(const Texture& t, float u, float v) {
    float x = (u - (float)floorInt(u)) * (float)t.w - 0.5f, y = (v - (float)floorInt(v)) * (float)t.h - 0.5f;
    int x0 = floorInt(x), y0 = floorInt(y);
    float tx = x - (float)x0, ty = y - (float)y0;
    int x1 = x0 + 1, y1 = y0 + 1;
    if (x0 < 0) x0 += t.w;
    if (y0 < 0) y0 += t.h;
    if (x1 >= t.w) x1 -= t.w;
    if (y1 >= t.h) y1 -= t.h;
    if (x0 >= t.w) x0 = t.w - 1;
    if (y0 >= t.h) y0 = t.h - 1;
    const uint8_t* b = t.bump.data();
    size_t W = (size_t)t.w;
    float a = b[(size_t)y0 * W + (size_t)x0] * (1 - tx) + b[(size_t)y0 * W + (size_t)x1] * tx;
    float c = b[(size_t)y1 * W + (size_t)x0] * (1 - tx) + b[(size_t)y1 * W + (size_t)x1] * tx;
    return (a * (1 - ty) + c * ty) * (1.0f / 255.0f);
}

/// Fills one triangle into the rows y0..y1 of the frame. Pixels are tested in the middle: a pixel belongs to the triangle when its centre is inside (the left and top edges count, the right and
/// bottom ones do not, so two triangles that share an edge never draw the same pixel twice).
inline void rasterTri(uint8_t* rgba, Frame& f, const Context& ctx, const Tri& t, int y0, int y1, bool blendedPass) {
    const Batch& b = f.batches[(size_t)t.batch];
    const Surface& s = b.s;
    const Texture* tex = b.tex;
    bool texOn = s.useTexture && tex && tex->w > 0 && tex->h > 0;
    bool bumpOn = t.bump && texOn && !tex->bump.empty();
    float alphaScale = s.opacity;
    int rowStart = std::max(t.minY, y0), rowEnd = std::min(t.maxY, y1);
    for (int y = rowStart; y <= rowEnd; y++) {
        float py = (float)y + 0.5f;
        // the span of the row: where the edges cross it
        float xl = 1e30f, xr = -1e30f;
        int crossings = 0;
        for (int e = 0; e < 3; e++) {
            int j = (e + 1) % 3;
            float ya = t.sy[e], yb = t.sy[j];
            if (ya == yb) continue;
            float lo = ya < yb ? ya : yb, hi = ya < yb ? yb : ya;
            if (py < lo || py >= hi) continue;
            float x = t.sx[e] + (py - ya) * (t.sx[j] - t.sx[e]) / (yb - ya);
            if (x < xl) xl = x;
            if (x > xr) xr = x;
            crossings++;
        }
        if (crossings < 2) continue;
        int xs = std::max(0, (int)std::ceil(xl - 0.5f)), xe = std::min(f.w - 1, (int)std::ceil(xr - 0.5f) - 1);
        if (xs > xe) continue;
        float dx0 = (float)xs + 0.5f - t.sx[0], dy0 = py - t.sy[0];
        float cur[kAttrs];
        for (int k = 0; k < kAttrs; k++) cur[k] = t.base[k] + t.gx[k] * dx0 + t.gy[k] * dy0;
        size_t di = (size_t)y * (size_t)f.w + (size_t)xs;
        for (int x = xs; x <= xe; x++, di++) {
            float z = cur[0];
            bool visible = z < f.depth[di] && cur[1] > 0;
            if (visible) {
                float k = 1.0f / cur[1];
                Vec3 p{cur[7] * k, cur[8] * k, cur[9] * k};
                Vec3 n = t.fn;
                if (s.smooth && !t.constNormal) {
                    float nx = cur[4] * k, ny = cur[5] * k, nz = cur[6] * k;
                    float l2 = nx * nx + ny * ny + nz * nz;
                    float inv = l2 > 1e-20f ? 1.0f / std::sqrt(l2) : 0.0f;
                    n = {nx * inv, ny * inv, nz * inv};
                }
                if (s.twoSided && dot(n, ctx.eye - p) < 0) n = n * -1.0f;
                Vec3 base = s.color;
                float alpha = alphaScale;
                float u = 0, v = 0;
                if (texOn) {
                    u = cur[2] * k; v = cur[3] * k;
                    float ta;
                    Vec3 tc = sampleTexture(*tex, u, v, ta);
                    if (s.cutoff > 0 && ta < s.cutoff) visible = false;
                    base = {base.x * tc.x, base.y * tc.y, base.z * tc.z};
                    alpha *= ta;
                    if (visible && bumpOn) {
                        // the slope of the heights along u and v over one texel (the strength says how high a full step of the heights is): tilt the normal against it
                        float du = 0.5f / (float)tex->w, dv = 0.5f / (float)tex->h;
                        float hu = (sampleBump(*tex, u + du, v) - sampleBump(*tex, u - du, v)) * s.bump;
                        float hv = (sampleBump(*tex, u, v + dv) - sampleBump(*tex, u, v - dv)) * s.bump;
                        Vec3 tilted = n - t.tangent * hu - t.bitangent * hv;
                        n = normalize(tilted);
                    }
                }
                if (visible) {
                    Vec3 col;
                    if (!s.lit) col = {base.x + s.emissive.x, base.y + s.emissive.y, base.z + s.emissive.z};
                    else {
                        Vec3 diffuse = ctx.ambient, spec{0, 0, 0};
                        Vec3 viewDir{0, 0, 0};
                        bool haveView = false;
                        for (const PreparedLight& L : ctx.lights) {
                            if (!(L.mask & s.layers)) continue;
                            Vec3 toLight;
                            float atten = 1.0f;
                            if (L.kind == LightDirectional) toLight = L.toLight;
                            else {
                                Vec3 d = L.pos - p;
                                float d2 = d.x * d.x + d.y * d.y + d.z * d.z;
                                float dist = std::sqrt(d2);
                                toLight = dist > 1e-12f ? d * (1.0f / dist) : Vec3{0, 1, 0};
                                float q = dist * L.invRange;
                                atten = 1.0f / (1.0f + q * q);
                            }
                            float nl = dot(n, toLight);
                            if (nl <= 0) continue;
                            float shadow = 1.0f;
                            if (L.shadow >= 0 && s.receiveShadows) shadow = shadowFactor(ctx.shadows[(size_t)L.shadow], L.pos, L.kind == LightDirectional, p, n);
                            if (shadow <= 0) continue;
                            float w = atten * shadow;
                            diffuse = diffuse + L.color * (nl * w);
                            if (s.specular > 0) {
                                if (!haveView) { viewDir = normalize(ctx.eye - p); haveView = true; }
                                Vec3 h = normalize(toLight + viewDir);
                                float nh = dot(n, h);
                                if (nh > 0) spec = spec + L.color * (w * s.specular * std::pow(nh, s.shininess));
                            }
                        }
                        col = {base.x * diffuse.x + spec.x + s.emissive.x, base.y * diffuse.y + spec.y + s.emissive.y, base.z * diffuse.z + spec.z + s.emissive.z};
                    }
                    uint8_t* px = rgba + di * 4;
                    auto toByte = [](float c) { return (uint8_t)std::min(255.0f, std::max(0.0f, c * 255.0f + 0.5f)); };
                    if (blendedPass && alpha < 1.0f && s.cutoff <= 0) {
                        if (alpha > 0) {
                            px[0] = (uint8_t)(px[0] + (toByte(col.x) - (int)px[0]) * alpha + (toByte(col.x) >= px[0] ? 0.5f : -0.5f));
                            px[1] = (uint8_t)(px[1] + (toByte(col.y) - (int)px[1]) * alpha + (toByte(col.y) >= px[1] ? 0.5f : -0.5f));
                            px[2] = (uint8_t)(px[2] + (toByte(col.z) - (int)px[2]) * alpha + (toByte(col.z) >= px[2] ? 0.5f : -0.5f));
                        }
                    } else {
                        px[0] = toByte(col.x); px[1] = toByte(col.y); px[2] = toByte(col.z); px[3] = 255;
                        f.depth[di] = z;
                    }
                }
            }
            for (int k = 0; k < kAttrs; k++) cur[k] += t.gx[k];
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Setting up: transform a mesh, cut it against the near plane, put its triangles on the screen
// ---------------------------------------------------------------------------------------------------------------------
struct ScreenVertex {
    float sx, sy, z, invW;
    float wx, wy, wz, nx, ny, nz, u, v;   // attributes divided by w (for perspective correction)
};

/// The plane of one attribute through three screen points: the value at vertex 0 and the change per pixel.
inline void planeOf(const float* sx, const float* sy, float a0, float a1, float a2, float invDet, float& base, float& gx, float& gy) {
    float d1 = a1 - a0, d2 = a2 - a0;
    float x1 = sx[1] - sx[0], x2 = sx[2] - sx[0], y1 = sy[1] - sy[0], y2 = sy[2] - sy[0];
    base = a0;
    gx = (d1 * y2 - d2 * y1) * invDet;
    gy = (d2 * x1 - d1 * x2) * invDet;
}

inline bool setupTri(Tri& t, const ScreenVertex& a, const ScreenVertex& b, const ScreenVertex& c, int height) {
    t.sx[0] = a.sx; t.sx[1] = b.sx; t.sx[2] = c.sx;
    t.sy[0] = a.sy; t.sy[1] = b.sy; t.sy[2] = c.sy;
    float det = (t.sx[1] - t.sx[0]) * (t.sy[2] - t.sy[0]) - (t.sx[2] - t.sx[0]) * (t.sy[1] - t.sy[0]);
    if (std::fabs(det) < 1e-9f) return false;
    float lo = std::min({a.sy, b.sy, c.sy}), hi = std::max({a.sy, b.sy, c.sy});
    t.minY = std::max(0, (int)std::ceil(lo - 0.5f));
    t.maxY = std::min(height - 1, (int)std::ceil(hi - 0.5f) - 1);
    if (t.minY > t.maxY) return false;
    float inv = 1.0f / det;
    const ScreenVertex* v[3] = {&a, &b, &c};
    auto P = [&](int k, float ScreenVertex::*m) { planeOf(t.sx, t.sy, a.*m, b.*m, c.*m, inv, t.base[k], t.gx[k], t.gy[k]); (void)v; };
    P(0, &ScreenVertex::z); P(1, &ScreenVertex::invW); P(2, &ScreenVertex::u); P(3, &ScreenVertex::v);
    P(4, &ScreenVertex::nx); P(5, &ScreenVertex::ny); P(6, &ScreenVertex::nz);
    P(7, &ScreenVertex::wx); P(8, &ScreenVertex::wy); P(9, &ScreenVertex::wz);
    return true;
}

/// Queues one mesh for the frame. `lights` are only used by `flush`; the camera places the mesh (and a billboard faces it).
inline void queueMesh(Frame& f, const CameraParams& cam, const Mesh& mesh, const ObjectParams& obj, const Surface& surf, const Texture* tex) {
    if (f.w <= 0 || f.h <= 0 || mesh.idx.size() < 3) return;
    // model = T(pos) * R * S * T(-pivot); a billboard replaces R by a turn towards the camera
    Mat4 rotation = rotationXYZ(obj.rx, obj.ry, obj.rz);
    if (surf.billboard != 0) {
        Vec3 toCam = cam.pos - obj.pos;
        if (surf.billboard == 2) toCam.y = 0;
        Vec3 zaxis = normalize(toCam);
        Vec3 xaxis = normalize(cross(Vec3{0, 1, 0}, zaxis));
        if (std::fabs(zaxis.y) > 0.9999f) xaxis = Vec3{1, 0, 0};
        Vec3 yaxis = cross(zaxis, xaxis);
        Mat4 b = Mat4::identity();
        b.at(0, 0) = xaxis.x; b.at(1, 0) = xaxis.y; b.at(2, 0) = xaxis.z;
        b.at(0, 1) = yaxis.x; b.at(1, 1) = yaxis.y; b.at(2, 1) = yaxis.z;
        b.at(0, 2) = zaxis.x; b.at(1, 2) = zaxis.y; b.at(2, 2) = zaxis.z;
        rotation = mul(b, rotationZ(obj.rz));
    }
    Mat4 model = mul(translation(obj.pos), mul(rotation, mul(scaling(obj.scale), translation(obj.pivot * -1.0f))));
    // view = inverse(T(cam) * R) = R^-1 * T(-cam)
    Mat4 rinv = mul(rotationZ(-cam.rz), mul(rotationX(-cam.rx), rotationY(-cam.ry)));
    Mat4 view = mul(rinv, translation(cam.pos * -1.0f));
    float aspect = (float)f.w / (float)f.h;
    float tf = 1.0f / std::tan(cam.fovY * 0.5f);
    float nearP = cam.nearPlane, farP = cam.farPlane;
    // projection (OpenGL style clip space: -w <= z <= w)
    Mat4 proj;
    for (int i = 0; i < 16; i++) proj.m[i] = 0;
    proj.at(0, 0) = tf / aspect; proj.at(1, 1) = tf;
    proj.at(2, 2) = (farP + nearP) / (nearP - farP); proj.at(2, 3) = 2 * farP * nearP / (nearP - farP);
    proj.at(3, 2) = -1;
    Mat4 vp = mul(proj, view);
    // the normal matrix: the inverse transpose of the model's 3x3 part. For rotation and a non uniform scale that is (R * S^-1).
    Vec3 invS{obj.scale.x != 0 ? 1.0f / obj.scale.x : 0, obj.scale.y != 0 ? 1.0f / obj.scale.y : 0, obj.scale.z != 0 ? 1.0f / obj.scale.z : 0};
    Mat4 normalM = mul(rotation, scaling(invS));

    f.batches.emplace_back();
    Batch& batch = f.batches.back();
    int batchIndex = (int)f.batches.size() - 1;
    batch.s = surf;
    batch.tex = (surf.useTexture && tex && tex->w > 0 && tex->h > 0) ? tex : nullptr;
    batch.mesh = &mesh;
    batch.blended = surf.cutoff <= 0 && (surf.opacity < 1.0f || (batch.tex && batch.tex->hasAlpha));
    Vec3 centre = transformPoint(model, (mesh.bmin + mesh.bmax) * 0.5f);
    batch.sortKey = length(centre - cam.pos);

    size_t vertexCount = mesh.pos.size() / 3;
    std::vector<ClipVertex>& cv = batch.cv;
    cv.resize(vertexCount);
    for (size_t i = 0; i < vertexCount; i++) {
        Vec3 wp = transformPoint(model, Vec3{mesh.pos[i * 3], mesh.pos[i * 3 + 1], mesh.pos[i * 3 + 2]});
        Vec3 wn = normalize(transformDir(normalM, Vec3{mesh.nrm[i * 3], mesh.nrm[i * 3 + 1], mesh.nrm[i * 3 + 2]}));
        ClipVertex& c = cv[i];
        c.wx = wp.x; c.wy = wp.y; c.wz = wp.z; c.nx = wn.x; c.ny = wn.y; c.nz = wn.z;
        float u = mesh.uv[i * 2], v = mesh.uv[i * 2 + 1];
        c.u = surf.tm[0] * u + surf.tm[1] * v + surf.tm[2];   // the texture mapping is applied to the corners: the rest is interpolated
        c.v = surf.tm[3] * u + surf.tm[4] * v + surf.tm[5];
        c.x = vp.at(0, 0) * wp.x + vp.at(0, 1) * wp.y + vp.at(0, 2) * wp.z + vp.at(0, 3);
        c.y = vp.at(1, 0) * wp.x + vp.at(1, 1) * wp.y + vp.at(1, 2) * wp.z + vp.at(1, 3);
        c.z = vp.at(2, 0) * wp.x + vp.at(2, 1) * wp.y + vp.at(2, 2) * wp.z + vp.at(2, 3);
        c.w = vp.at(3, 0) * wp.x + vp.at(3, 1) * wp.y + vp.at(3, 2) * wp.z + vp.at(3, 3);
    }
    auto toScreen = [&](const ClipVertex& c) {
        ScreenVertex s;
        float iw = 1.0f / c.w;
        s.sx = (c.x * iw * 0.5f + 0.5f) * (float)f.w;
        s.sy = (1.0f - (c.y * iw * 0.5f + 0.5f)) * (float)f.h;
        s.z = c.z * iw;
        s.invW = iw;
        s.wx = c.wx * iw; s.wy = c.wy * iw; s.wz = c.wz * iw;
        s.nx = c.nx * iw; s.ny = c.ny * iw; s.nz = c.nz * iw;
        s.u = c.u * iw; s.v = c.v * iw;
        return s;
    };
    bool bumpWanted = surf.bump != 0 && batch.tex && !batch.tex->bump.empty();
    // a blended object that is drawn from both sides shows its far side first
    int sweeps = (batch.blended && surf.twoSided) ? 2 : 1;
    std::vector<Tri>& out = batch.blended ? f.blended : f.opaque;
    for (int sweep = 0; sweep < sweeps; sweep++)
        for (size_t t = 0; t + 2 < mesh.idx.size(); t += 3) {
            uint32_t i0 = mesh.idx[t], i1 = mesh.idx[t + 1], i2 = mesh.idx[t + 2];
            if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount) continue;
            Vec3 wa{cv[i0].wx, cv[i0].wy, cv[i0].wz}, wb{cv[i1].wx, cv[i1].wy, cv[i1].wz}, wc{cv[i2].wx, cv[i2].wy, cv[i2].wz};
            Vec3 fn = normalize(cross(wb - wa, wc - wa));
            bool facing = dot(fn, cam.pos - wa) > 0;
            if (sweeps == 2) { if ((sweep == 0) == facing) continue; }          // far side first, then the near side
            else if (!surf.twoSided && !facing) continue;                       // back face
            // clip against the near plane (z >= -w), the other planes are handled by the rows and columns
            ClipVertex poly[4];
            int count = 0;
            const ClipVertex* in[3] = {&cv[i0], &cv[i1], &cv[i2]};
            for (int e = 0; e < 3; e++) {
                const ClipVertex& cur = *in[e];
                const ClipVertex& nxt = *in[(e + 1) % 3];
                float dc = cur.z + cur.w, dn = nxt.z + nxt.w;
                bool curIn = dc >= 0, nxtIn = dn >= 0;
                if (curIn) poly[count++] = cur;
                if (curIn != nxtIn) poly[count++] = lerpVertex(cur, nxt, dc / (dc - dn));
            }
            if (count < 3) continue;
            ScreenVertex sv[4];
            for (int i = 0; i < count; i++) sv[i] = toScreen(poly[i]);
            // a face whose three vertex normals agree (a plane, the side of a box) has one normal
            bool constNormal = dot(Vec3{cv[i0].nx, cv[i0].ny, cv[i0].nz}, Vec3{cv[i1].nx, cv[i1].ny, cv[i1].nz}) > 0.99999f && dot(Vec3{cv[i0].nx, cv[i0].ny, cv[i0].nz}, Vec3{cv[i2].nx, cv[i2].ny, cv[i2].nz}) > 0.99999f;
            Vec3 tangent{1, 0, 0}, bitangent{0, 0, 1};
            if (bumpWanted) {
                // the directions of increasing u and v on the face
                Vec3 e1 = wb - wa, e2 = wc - wa;
                float du1 = cv[i1].u - cv[i0].u, dv1 = cv[i1].v - cv[i0].v, du2 = cv[i2].u - cv[i0].u, dv2 = cv[i2].v - cv[i0].v;
                float det = du1 * dv2 - du2 * dv1;
                if (std::fabs(det) > 1e-12f) {
                    float r = 1.0f / det;
                    tangent = normalize((e1 * dv2 - e2 * dv1) * r);
                    bitangent = normalize((e2 * du1 - e1 * du2) * r);
                }
            }
            for (int i = 1; i + 1 < count; i++) {
                Tri tri;
                tri.batch = batchIndex;
                if (!setupTri(tri, sv[0], sv[i], sv[i + 1], f.h)) continue;
                tri.fn = fn;
                tri.constNormal = constNormal;
                tri.bump = bumpWanted;
                tri.tangent = tangent; tri.bitangent = bitangent;
                out.push_back(tri);
            }
        }
}

// ---------------------------------------------------------------------------------------------------------------------
// Shadow maps: the depth of what casts a shadow, seen from the light (an orthographic view for a directional light, six views for a point light)
// ---------------------------------------------------------------------------------------------------------------------
inline Mat4 lookAtMatrix(Vec3 eye, Vec3 forward, Vec3 upHint) {
    Vec3 z = normalize(forward * -1.0f);
    Vec3 x = cross(upHint, z);
    if (length(x) < 1e-4f) x = cross(Vec3{1, 0, 0}, z);
    x = normalize(x);
    Vec3 y = cross(z, x);
    Mat4 r = Mat4::identity();
    r.at(0, 0) = x.x; r.at(0, 1) = x.y; r.at(0, 2) = x.z; r.at(0, 3) = -dot(x, eye);
    r.at(1, 0) = y.x; r.at(1, 1) = y.y; r.at(1, 2) = y.z; r.at(1, 3) = -dot(y, eye);
    r.at(2, 0) = z.x; r.at(2, 1) = z.y; r.at(2, 2) = z.z; r.at(2, 3) = -dot(z, eye);
    return r;
}

struct DepthVertex { float sx, sy, d; };

inline void rasterDepth(ShadowMap& m, const DepthVertex& a, const DepthVertex& b, const DepthVertex& c) {
    float sx[3] = {a.sx, b.sx, c.sx}, sy[3] = {a.sy, b.sy, c.sy};
    float det = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
    if (std::fabs(det) < 1e-9f) return;
    float inv = 1.0f / det;
    // perspective: 1/d is linear on the screen; orthographic: d is
    float v0 = m.ortho ? a.d : 1.0f / a.d, v1 = m.ortho ? b.d : 1.0f / b.d, v2 = m.ortho ? c.d : 1.0f / c.d;
    float base, gx, gy;
    planeOf(sx, sy, v0, v1, v2, inv, base, gx, gy);
    int minY = std::max(0, (int)std::ceil(std::min({sy[0], sy[1], sy[2]}) - 0.5f)), maxY = std::min(m.res - 1, (int)std::ceil(std::max({sy[0], sy[1], sy[2]}) - 0.5f) - 1);
    for (int y = minY; y <= maxY; y++) {
        float py = (float)y + 0.5f, xl = 1e30f, xr = -1e30f;
        int crossings = 0;
        for (int e = 0; e < 3; e++) {
            int j = (e + 1) % 3;
            float ya = sy[e], yb = sy[j];
            if (ya == yb) continue;
            float lo = ya < yb ? ya : yb, hi = ya < yb ? yb : ya;
            if (py < lo || py >= hi) continue;
            float x = sx[e] + (py - ya) * (sx[j] - sx[e]) / (yb - ya);
            if (x < xl) xl = x;
            if (x > xr) xr = x;
            crossings++;
        }
        if (crossings < 2) continue;
        int xs = std::max(0, (int)std::ceil(xl - 0.5f)), xe = std::min(m.res - 1, (int)std::ceil(xr - 0.5f) - 1);
        float value = base + gx * ((float)xs + 0.5f - sx[0]) + gy * (py - sy[0]);
        float* row = &m.depth[(size_t)y * (size_t)m.res];
        for (int x = xs; x <= xe; x++, value += gx) {
            float d = m.ortho ? value : (value > 0 ? 1.0f / value : 1e30f);
            if (d < row[x]) row[x] = d;
        }
    }
}

/// Draws the casters (every batch that casts shadows) into the map. The view of the map is set; the triangles are cut against its near plane when it is a perspective one.
inline void renderShadowMap(ShadowMap& m, const Frame& f, float nearPlane) {
    m.depth.assign((size_t)m.res * (size_t)m.res, 1e30f);
    for (const Batch& b : f.batches) {
        if (!b.s.castShadows || b.blended || !b.mesh) continue;   // transparent objects cast no shadow
        const Mesh& mesh = *b.mesh;
        std::vector<Vec3> view(b.cv.size());
        for (size_t i = 0; i < b.cv.size(); i++) view[i] = transformPoint(m.view, Vec3{b.cv[i].wx, b.cv[i].wy, b.cv[i].wz});
        for (size_t t = 0; t + 2 < mesh.idx.size(); t += 3) {
            uint32_t ix[3] = {mesh.idx[t], mesh.idx[t + 1], mesh.idx[t + 2]};
            if (ix[0] >= view.size() || ix[1] >= view.size() || ix[2] >= view.size()) continue;
            Vec3 poly[4];
            int count = 0;
            for (int e = 0; e < 3; e++) {
                Vec3 cur = view[ix[e]], nxt = view[ix[(e + 1) % 3]];
                if (m.ortho) { poly[count++] = cur; continue; }
                float dc = -cur.z - nearPlane, dn = -nxt.z - nearPlane;
                bool curIn = dc >= 0, nxtIn = dn >= 0;
                if (curIn) poly[count++] = cur;
                if (curIn != nxtIn) { float k = dc / (dc - dn); poly[count++] = cur + (nxt - cur) * k; }
            }
            if (count < 3) continue;
            DepthVertex dv[4];
            for (int i = 0; i < count; i++) {
                float d = -poly[i].z;
                float u, v;
                if (m.ortho) { u = poly[i].x * m.sx + m.ox; v = 1.0f - (poly[i].y * m.sy + m.oy); }
                else { float k = m.f * 0.5f / d; u = poly[i].x * k + 0.5f; v = 1.0f - (poly[i].y * k + 0.5f); }
                dv[i] = {u * (float)m.res, v * (float)m.res, d};
            }
            for (int i = 1; i + 1 < count; i++) rasterDepth(m, dv[0], dv[i], dv[i + 1]);
        }
    }
}

inline int workerCount(const FlushSettings& st, size_t rows) {
#if defined(FIRE_S3D_NO_THREADS) || defined(__EMSCRIPTEN__) || defined(ESP_PLATFORM)
    (void)st; (void)rows;
    return 1;
#else
    int n = st.threads;
    if (n <= 0) { n = (int)std::thread::hardware_concurrency(); if (n <= 0) n = 1; if (n > 8) n = 8; }
    if ((size_t)n > rows) n = (int)rows;
    return n < 1 ? 1 : n;
#endif
}

/// Calls fn(0) .. fn(count - 1), spread over `workers` threads (the calls must not depend on each other).
template <class F>
inline void parallelFor(int count, int workers, F fn) {
#if !defined(FIRE_S3D_NO_THREADS) && !defined(__EMSCRIPTEN__) && !defined(ESP_PLATFORM)
    if (workers > 1 && count > 1) {
        std::atomic<int> next{0};
        auto run = [&]() { for (int i = next++; i < count; i = next++) fn(i); };
        std::vector<std::thread> pool;
        for (int i = 1; i < workers && i < count; i++) pool.emplace_back(run);
        run();
        for (std::thread& th : pool) th.join();
        return;
    }
#endif
    (void)workers;
    for (int i = 0; i < count; i++) fn(i);
}

/// Builds the shadow maps of the lights that cast shadows: one orthographic map around the casters for a directional light, six for a point light.
inline void buildShadows(Context& ctx, const Frame& f, const std::vector<Light>& lights, const FlushSettings& st) {
    ctx.shadows.clear();
    if (!st.shadows) return;
    // the casters' bounds
    bool any = false;
    Vec3 lo{1e30f, 1e30f, 1e30f}, hi{-1e30f, -1e30f, -1e30f};
    for (const Batch& b : f.batches) {
        if (!b.s.castShadows || b.blended) continue;
        for (const ClipVertex& v : b.cv) {
            lo = {std::min(lo.x, v.wx), std::min(lo.y, v.wy), std::min(lo.z, v.wz)};
            hi = {std::max(hi.x, v.wx), std::max(hi.y, v.wy), std::max(hi.z, v.wz)};
            any = true;
        }
    }
    if (!any) return;
    int res = std::max(64, std::min(4096, st.shadowResolution));
    for (size_t li = 0; li < lights.size(); li++) {
        const Light& l = lights[li];
        if (!l.castShadows || l.kind == LightAmbient) continue;
        ShadowSet set;
        if (l.kind == LightDirectional) {
            Vec3 forward = normalize(l.dir);
            Vec3 up = std::fabs(forward.y) > 0.99f ? Vec3{1, 0, 0} : Vec3{0, 1, 0};
            Mat4 view = lookAtMatrix(Vec3{0, 0, 0}, forward, up);
            // the bounds of the casters in the view of the light
            Vec3 vlo{1e30f, 1e30f, 1e30f}, vhi{-1e30f, -1e30f, -1e30f};
            for (int c = 0; c < 8; c++) {
                Vec3 corner{(c & 1) ? hi.x : lo.x, (c & 2) ? hi.y : lo.y, (c & 4) ? hi.z : lo.z};
                Vec3 q = transformPoint(view, corner);
                vlo = {std::min(vlo.x, q.x), std::min(vlo.y, q.y), std::min(vlo.z, q.z)};
                vhi = {std::max(vhi.x, q.x), std::max(vhi.y, q.y), std::max(vhi.z, q.z)};
            }
            float margin = 0.05f * std::max(vhi.x - vlo.x, vhi.y - vlo.y) + 0.01f;
            vlo.x -= margin; vlo.y -= margin; vhi.x += margin; vhi.y += margin;
            ShadowMap& m = set.maps[0];
            set.count = 1;
            m.res = res; m.ortho = true; m.view = view;
            // the map is square: the longer side decides the scale, so that a texel is as wide as it is high
            float extent = std::max(vhi.x - vlo.x, vhi.y - vlo.y);
            m.sx = m.sy = 1.0f / extent;
            m.ox = -vlo.x * m.sx; m.oy = -vlo.y * m.sy;
        } else {
            static const Vec3 dirs[6] = {{1, 0, 0}, {-1, 0, 0}, {0, 1, 0}, {0, -1, 0}, {0, 0, 1}, {0, 0, -1}};
            static const Vec3 ups[6] = {{0, 1, 0}, {0, 1, 0}, {0, 0, 1}, {0, 0, 1}, {0, 1, 0}, {0, 1, 0}};
            set.count = 6;
            for (int face = 0; face < 6; face++) {
                ShadowMap& m = set.maps[face];
                m.res = std::max(64, res / 2); m.ortho = false;
                m.view = lookAtMatrix(l.pos, dirs[face], ups[face]);
                m.f = 1.0f / std::tan(46.0f * 3.14159265358979f / 180.0f);   // a little more than 90 degrees: the faces overlap at the seams
            }
        }
        ctx.shadows.push_back(set);   // the caller counts: the n-th light that casts shadows has the n-th set
    }
    std::vector<ShadowMap*> maps;
    for (ShadowSet& set : ctx.shadows) for (int i = 0; i < set.count; i++) maps.push_back(&set.maps[i]);
    parallelFor((int)maps.size(), workerCount(st, maps.size()), [&](int i) { renderShadowMap(*maps[(size_t)i], f, 0.05f); });
}

inline void prepareContext(Context& ctx, const CameraParams& cam, const std::vector<Light>& lights, const FlushSettings& st, const Frame& f) {
    ctx.eye = cam.pos;
    ctx.ambient = Vec3{0, 0, 0};
    ctx.lights.clear();
    buildShadows(ctx, f, lights, st);
    int shadowIndex = 0;
    for (const Light& l : lights) {
        if (l.kind == LightAmbient) { ctx.ambient = ctx.ambient + l.color * l.intensity; continue; }
        PreparedLight p;
        p.kind = l.kind;
        p.color = l.color * l.intensity;
        p.pos = l.pos;
        p.toLight = normalize(l.dir * -1.0f);
        p.invRange = 1.0f / (l.range > 1e-6f ? l.range : 1e-6f);
        p.mask = l.mask;
        p.shadow = -1;
        if (l.castShadows && st.shadows && (size_t)shadowIndex < ctx.shadows.size()) p.shadow = shadowIndex++;
        ctx.lights.push_back(p);
    }
}


// ---------------------------------------------------------------------------------------------------------------------
// Flush: rasterise the queue
// ---------------------------------------------------------------------------------------------------------------------
/// Draws everything that was queued with `queueMesh` into `rgba` (the camera's picture, already cleared) and empties the queue. Opaque objects first (in the order they were queued), then the
/// transparent ones from the far to the near. The rows are shared between threads in bands of 16; a pixel is only ever touched by the thread of its band, so the result does not depend on the
/// number of threads.
inline void flush(uint8_t* rgba, Frame& f, const CameraParams& cam, const std::vector<Light>& lights, const FlushSettings& st) {
    if (f.w <= 0 || f.h <= 0 || (f.opaque.empty() && f.blended.empty())) { f.batches.clear(); f.opaque.clear(); f.blended.clear(); return; }
    Context ctx;
    prepareContext(ctx, cam, lights, st, f);
    // far to near for the transparent ones (stable: objects at the same distance keep their order)
    std::stable_sort(f.blended.begin(), f.blended.end(), [&](const Tri& a, const Tri& b) { return f.batches[(size_t)a.batch].sortKey > f.batches[(size_t)b.batch].sortKey; });
    const int band = 16;
    int bands = (f.h + band - 1) / band;
    auto doBand = [&](int bi) {
        int y0 = bi * band, y1 = std::min(f.h - 1, y0 + band - 1);
        for (const Tri& t : f.opaque) if (t.maxY >= y0 && t.minY <= y1) rasterTri(rgba, f, ctx, t, y0, y1, false);
        for (const Tri& t : f.blended) if (t.maxY >= y0 && t.minY <= y1) rasterTri(rgba, f, ctx, t, y0, y1, true);
    };
    int workers = (size_t)f.w * (size_t)f.h < 20000 ? 1 : workerCount(st, (size_t)bands);
    parallelFor(bands, workers, doBand);
    f.batches.clear(); f.opaque.clear(); f.blended.clear();
}


// ---------------------------------------------------------------------------------------------------------------------
// Meshes: the templates (a unit size, centred on the origin) and the Wavefront OBJ reader
// ---------------------------------------------------------------------------------------------------------------------
enum TemplateKind { TemplateBox = 0, TemplateSphere = 1, TemplateCylinder = 2, TemplateCone = 3, TemplatePlane = 4, TemplateQuad = 5 };

inline void addVertex(Mesh& m, float x, float y, float z, float u, float v) {
    m.pos.push_back(x); m.pos.push_back(y); m.pos.push_back(z);
    m.uv.push_back(u); m.uv.push_back(v);
}
inline void addTriangle(Mesh& m, uint32_t a, uint32_t b, uint32_t c) { m.idx.push_back(a); m.idx.push_back(b); m.idx.push_back(c); }

/// The template meshes: counter clockwise seen from outside. A box is 1 x 1 x 1, a sphere has the diameter 1, a cylinder and a cone are 1 high with the diameter 1, a plane is 1 x 1 in the XZ plane
/// facing up (+Y). `detail` is the number of segments around (sphere, cylinder, cone), at least 3.
inline Mesh* makeTemplate(int kind, int detail) {
    auto m = new Mesh();
    const float pi = 3.14159265358979f;
    int seg = detail < 3 ? 3 : (detail > 256 ? 256 : detail);
    switch (kind) {
        case TemplateBox: {
            struct Face { float p[4][3]; };
            static const Face faces[6] = {
                {{{-.5f, -.5f, .5f}, {.5f, -.5f, .5f}, {.5f, .5f, .5f}, {-.5f, .5f, .5f}}},     // +Z (front)
                {{{.5f, -.5f, -.5f}, {-.5f, -.5f, -.5f}, {-.5f, .5f, -.5f}, {.5f, .5f, -.5f}}}, // -Z
                {{{.5f, -.5f, .5f}, {.5f, -.5f, -.5f}, {.5f, .5f, -.5f}, {.5f, .5f, .5f}}},     // +X
                {{{-.5f, -.5f, -.5f}, {-.5f, -.5f, .5f}, {-.5f, .5f, .5f}, {-.5f, .5f, -.5f}}}, // -X
                {{{-.5f, .5f, .5f}, {.5f, .5f, .5f}, {.5f, .5f, -.5f}, {-.5f, .5f, -.5f}}},     // +Y (top)
                {{{-.5f, -.5f, -.5f}, {.5f, -.5f, -.5f}, {.5f, -.5f, .5f}, {-.5f, -.5f, .5f}}}, // -Y (bottom)
            };
            static const float uv[4][2] = {{0, 1}, {1, 1}, {1, 0}, {0, 0}};
            for (const Face& f : faces) {
                uint32_t base = (uint32_t)(m->pos.size() / 3);
                for (int i = 0; i < 4; i++) addVertex(*m, f.p[i][0], f.p[i][1], f.p[i][2], uv[i][0], uv[i][1]);
                addTriangle(*m, base, base + 1, base + 2);
                addTriangle(*m, base, base + 2, base + 3);
            }
            break;
        }
        case TemplatePlane: {
            addVertex(*m, -.5f, 0, .5f, 0, 1); addVertex(*m, .5f, 0, .5f, 1, 1); addVertex(*m, .5f, 0, -.5f, 1, 0); addVertex(*m, -.5f, 0, -.5f, 0, 0);
            addTriangle(*m, 0, 1, 2); addTriangle(*m, 0, 2, 3);
            break;
        }
        case TemplateQuad: {   // 1 x 1 in the XY plane facing +Z: the shape of a billboard
            addVertex(*m, -.5f, -.5f, 0, 0, 1); addVertex(*m, .5f, -.5f, 0, 1, 1); addVertex(*m, .5f, .5f, 0, 1, 0); addVertex(*m, -.5f, .5f, 0, 0, 0);
            addTriangle(*m, 0, 1, 2); addTriangle(*m, 0, 2, 3);
            break;
        }
        case TemplateSphere: {
            int rings = seg / 2 < 2 ? 2 : seg / 2;
            for (int i = 0; i <= rings; i++) {
                float th = pi * (float)i / (float)rings;
                for (int j = 0; j <= seg; j++) {
                    float ph = 2 * pi * (float)j / (float)seg;
                    addVertex(*m, 0.5f * std::sin(th) * std::cos(ph), 0.5f * std::cos(th), 0.5f * std::sin(th) * std::sin(ph), (float)j / (float)seg, (float)i / (float)rings);
                }
            }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < seg; j++) {
                    uint32_t a = (uint32_t)(i * (seg + 1) + j), b = a + 1, c = a + (uint32_t)(seg + 1), d = c + 1;
                    if (i != 0) addTriangle(*m, a, b, c);
                    if (i != rings - 1) addTriangle(*m, b, d, c);
                }
            break;
        }
        case TemplateCylinder:
        case TemplateCone: {
            bool cone = kind == TemplateCone;
            // the side: two rings of vertices (the top ring of a cone collapses to the apex, one vertex per segment keeps the normals sane)
            for (int j = 0; j <= seg; j++) {
                float ph = 2 * pi * (float)j / (float)seg, cx = std::cos(ph), sz = std::sin(ph), u = (float)j / (float)seg;
                addVertex(*m, 0.5f * cx, -0.5f, 0.5f * sz, u, 1);
                if (cone) addVertex(*m, 0, 0.5f, 0, u, 0); else addVertex(*m, 0.5f * cx, 0.5f, 0.5f * sz, u, 0);
            }
            for (int j = 0; j < seg; j++) {
                uint32_t a = (uint32_t)(2 * j), b = a + 1, c = a + 2, d = a + 3;   // a bottom, b top, c next bottom, d next top
                addTriangle(*m, a, b, c);
                if (!cone) addTriangle(*m, c, b, d);
            }
            // the caps (the bottom one always, the top one for the cylinder): a fan around a centre vertex
            for (int cap = 0; cap < (cone ? 1 : 2); cap++) {
                float y = cap == 0 ? -0.5f : 0.5f;
                uint32_t centre = (uint32_t)(m->pos.size() / 3);
                addVertex(*m, 0, y, 0, 0.5f, 0.5f);
                for (int j = 0; j <= seg; j++) {
                    float ph = 2 * pi * (float)j / (float)seg;
                    addVertex(*m, 0.5f * std::cos(ph), y, 0.5f * std::sin(ph), 0.5f + 0.5f * std::cos(ph), 0.5f + 0.5f * std::sin(ph));
                }
                for (int j = 0; j < seg; j++) {
                    uint32_t a = centre + 1 + (uint32_t)j, b = a + 1;
                    if (cap == 0) addTriangle(*m, centre, a, b); else addTriangle(*m, centre, b, a);
                }
            }
            break;
        }
        default:
            break;
    }
    m->finish();
    return m;
}

/// Reads a Wavefront OBJ: `v`, `vt` and `f` (triangles, quads and polygons are fanned; `v`, `v/vt`, `v//vn` and `v/vt/vn` are understood, negative indices count from the end). `vn` is ignored:
/// the normals are computed (smooth). null when nothing usable is in the text.
inline Mesh* parseObj(const std::string& text) {
    std::vector<float> vp, vt;
    auto m = new Mesh();
    std::vector<std::pair<int64_t, int64_t>> keys;   // (v, vt) pairs of the vertices made so far
    auto vertexFor = [&](int64_t vi, int64_t ti) -> int64_t {
        for (size_t k = 0; k < keys.size(); k++) if (keys[k].first == vi && keys[k].second == ti) return (int64_t)k;
        size_t nv = vp.size() / 3;
        if (vi < 0) vi += (int64_t)nv + 1;
        if (vi < 1 || (size_t)vi > nv) return -1;
        float u = 0, v = 0;
        if (ti != 0) {
            size_t nt = vt.size() / 2;
            int64_t t = ti < 0 ? ti + (int64_t)nt + 1 : ti;
            if (t >= 1 && (size_t)t <= nt) { u = vt[(size_t)(t - 1) * 2]; v = 1.0f - vt[(size_t)(t - 1) * 2 + 1]; }
        }
        addVertex(*m, vp[(size_t)(vi - 1) * 3], vp[(size_t)(vi - 1) * 3 + 1], vp[(size_t)(vi - 1) * 3 + 2], u, v);
        keys.push_back({vi, ti});
        return (int64_t)keys.size() - 1;
    };
    size_t pos = 0;
    while (pos < text.size()) {
        size_t end = text.find('\n', pos);
        if (end == std::string::npos) end = text.size();
        std::string line = text.substr(pos, end - pos);
        pos = end + 1;
        size_t hash = line.find('#');
        if (hash != std::string::npos) line.resize(hash);
        const char* p = line.c_str();
        while (*p == ' ' || *p == '\t' || *p == '\r') p++;
        if (p[0] == 'v' && (p[1] == ' ' || p[1] == '\t')) {
            float x = 0, y = 0, z = 0;
            if (std::sscanf(p + 1, "%f %f %f", &x, &y, &z) >= 3) { vp.push_back(x); vp.push_back(y); vp.push_back(z); }
        } else if (p[0] == 'v' && p[1] == 't') {
            float u = 0, v = 0;
            if (std::sscanf(p + 2, "%f %f", &u, &v) >= 2) { vt.push_back(u); vt.push_back(v); }
        } else if (p[0] == 'f' && (p[1] == ' ' || p[1] == '\t')) {
            std::vector<int64_t> face;
            const char* q = p + 1;
            while (*q) {
                while (*q == ' ' || *q == '\t' || *q == '\r') q++;
                if (!*q) break;
                char* e;
                long vi = std::strtol(q, &e, 10);
                if (e == q) break;
                long ti = 0;
                q = e;
                if (*q == '/') {
                    q++;
                    if (*q != '/') { ti = std::strtol(q, &e, 10); q = e; }
                    if (*q == '/') { q++; std::strtol(q, &e, 10); q = e; }
                }
                int64_t id = vertexFor(vi, ti);
                if (id >= 0) face.push_back(id);
            }
            for (size_t k = 1; k + 1 < face.size(); k++) addTriangle(*m, (uint32_t)face[0], (uint32_t)face[k], (uint32_t)face[k + 1]);
        }
    }
    if (m->idx.empty()) { delete m; return nullptr; }
    m->finish();
    return m;
}

/// The yaw and pitch (radians) of a camera at `from` that looks at `to`: rx (pitch) and ry (yaw), with the convention of this renderer (looking along -Z at rx = ry = 0).
inline void lookAngles(Vec3 from, Vec3 to, float& rx, float& ry) {
    Vec3 d = to - from;
    float horizontal = std::sqrt(d.x * d.x + d.z * d.z);
    ry = std::atan2(-d.x, -d.z);
    rx = std::atan2(d.y, horizontal);
}

}  // namespace s3d
}  // namespace fire
#endif  // FIRE_S3D_ENGINE_HPP
