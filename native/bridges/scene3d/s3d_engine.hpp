// fire scene3d: the software renderer behind `#import "scene3d"` (docs/SCENE3D.md). Plain C++ that knows nothing of fire: triangles go in, RGBA pixels come out.
//
// What it does: a perspective camera (vertical field of view), the model matrix of an object from its position, rotation, scale and pivot, clipping against the near plane, back-face
// culling, a depth buffer (1/w interpolation of z in screen space), perspective-correct interpolation of the texture coordinates, the normal and the world position, textures (repeat,
// bilinear), and Blinn-Phong lighting with an ambient, directional and point lights per pixel. A mesh with smooth normals is shaded with the interpolated vertex normal, a flat one with the
// normal of its face.
//
// One implementation for everything: the native build includes it and the virtual machine runs it in a shared library (native/abi/fire_pkg_abi.h), so a picture is the same in both.
// Right handed, Y up; a camera without rotation looks along -Z; angles in radians, lengths in meters (the fire side converts the units).
#pragma once
#ifndef FIRE_S3D_ENGINE_HPP
#define FIRE_S3D_ENGINE_HPP

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
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
};

enum LightKind { LightAmbient = 0, LightDirectional = 1, LightPoint = 2 };
struct Light {
    int kind = LightAmbient;
    Vec3 color{1, 1, 1};
    float intensity = 1;
    Vec3 pos;          // point
    Vec3 dir{0, 0, -1};// directional: the direction the light travels
    float range = 10;  // point: the distance at which it has half its strength
};

/// The target: RGBA pixels (owned by the caller) and a depth buffer.
struct Frame {
    int w = 0, h = 0;
    std::vector<float> depth;
    void resize(int nw, int nh) { w = nw; h = nh; depth.assign((size_t)nw * (size_t)nh, 1e30f); }
    void clearDepth() { std::fill(depth.begin(), depth.end(), 1e30f); }
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
};

struct ObjectParams {
    Vec3 pos, scale{1, 1, 1}, pivot;   // the pivot is a point of the mesh (in mesh space) that sits at `pos`
    float rx = 0, ry = 0, rz = 0;
};

inline void clearPixels(uint8_t* rgba, int w, int h, uint32_t color) {
    uint8_t r = color & 255, g = (color >> 8) & 255, b = (color >> 16) & 255, a = (color >> 24) & 255;
    for (size_t i = 0, n = (size_t)w * (size_t)h; i < n; i++) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a; }
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

struct ScreenVertex {
    float sx, sy, z, invW;
    float wx, wy, wz, nx, ny, nz, u, v;   // attributes divided by w (for perspective correction)
};

inline Vec3 sampleTexture(const Texture& t, float u, float v, float& alpha) {
    // repeat, bilinear; v = 0 is the top row of the picture
    float fu = u - std::floor(u), fv = v - std::floor(v);
    float x = fu * t.w - 0.5f, y = fv * t.h - 0.5f;
    int x0 = (int)std::floor(x), y0 = (int)std::floor(y);
    float tx = x - x0, ty = y - y0;
    auto texel = [&](int xi, int yi, float* c) {
        xi = ((xi % t.w) + t.w) % t.w;
        yi = ((yi % t.h) + t.h) % t.h;
        const uint8_t* p = &t.rgba[((size_t)yi * (size_t)t.w + (size_t)xi) * 4];
        c[0] = p[0]; c[1] = p[1]; c[2] = p[2]; c[3] = p[3];
    };
    float c00[4], c10[4], c01[4], c11[4];
    texel(x0, y0, c00); texel(x0 + 1, y0, c10); texel(x0, y0 + 1, c01); texel(x0 + 1, y0 + 1, c11);
    float out[4];
    for (int i = 0; i < 4; i++) {
        float top = c00[i] + (c10[i] - c00[i]) * tx, bottom = c01[i] + (c11[i] - c01[i]) * tx;
        out[i] = top + (bottom - top) * ty;
    }
    alpha = out[3] / 255.0f;
    return {out[0] / 255.0f, out[1] / 255.0f, out[2] / 255.0f};
}

/// The lighting of one point: `color` is the base colour (surface times texture), `n` the unit normal towards the viewer's side, `p` the position, `eye` the camera.
inline Vec3 shade(Vec3 color, Vec3 n, Vec3 p, Vec3 eye, const Surface& s, const std::vector<Light>& lights) {
    Vec3 ambient{0, 0, 0}, diffuse{0, 0, 0}, spec{0, 0, 0};
    Vec3 v = normalize(eye - p);
    for (const Light& l : lights) {
        if (l.kind == LightAmbient) { ambient = ambient + l.color * l.intensity; continue; }
        Vec3 toLight;
        float atten = 1.0f;
        if (l.kind == LightDirectional) toLight = normalize(l.dir * -1.0f);
        else {
            Vec3 d = l.pos - p;
            float dist = length(d);
            toLight = dist > 1e-12f ? d * (1.0f / dist) : Vec3{0, 1, 0};
            float q = dist / (l.range > 1e-6f ? l.range : 1e-6f);
            atten = 1.0f / (1.0f + q * q);
        }
        float nl = dot(n, toLight);
        if (nl <= 0) continue;
        diffuse = diffuse + l.color * (l.intensity * atten * nl);
        if (s.specular > 0) {
            Vec3 h = normalize(toLight + v);
            float nh = std::max(0.0f, dot(n, h));
            spec = spec + l.color * (l.intensity * atten * s.specular * std::pow(nh, s.shininess));
        }
    }
    return {color.x * (ambient.x + diffuse.x) + spec.x + s.emissive.x, color.y * (ambient.y + diffuse.y) + spec.y + s.emissive.y, color.z * (ambient.z + diffuse.z) + spec.z + s.emissive.z};
}

inline void rasterTriangle(uint8_t* rgba, Frame& f, const ScreenVertex& a, const ScreenVertex& b, const ScreenVertex& c, const Surface& s, const Texture* tex, const Vec3& eye,
                           const std::vector<Light>& lights, Vec3 faceNormal) {
    float area = (b.sx - a.sx) * (c.sy - a.sy) - (b.sy - a.sy) * (c.sx - a.sx);
    if (std::fabs(area) < 1e-12f) return;
    bool backFacing = area < 0;   // screen y points down, so a front face (counter clockwise in the world) has a negative area here ... see the caller: it passes the orientation it wants
    (void)backFacing;
    int minX = std::max(0, (int)std::floor(std::min({a.sx, b.sx, c.sx})));
    int maxX = std::min(f.w - 1, (int)std::ceil(std::max({a.sx, b.sx, c.sx})));
    int minY = std::max(0, (int)std::floor(std::min({a.sy, b.sy, c.sy})));
    int maxY = std::min(f.h - 1, (int)std::ceil(std::max({a.sy, b.sy, c.sy})));
    float invArea = 1.0f / area;
    for (int y = minY; y <= maxY; y++) {
        float py = y + 0.5f;
        for (int x = minX; x <= maxX; x++) {
            float px = x + 0.5f;
            float w0 = ((b.sx - px) * (c.sy - py) - (b.sy - py) * (c.sx - px)) * invArea;   // weight of a
            float w1 = ((c.sx - px) * (a.sy - py) - (c.sy - py) * (a.sx - px)) * invArea;   // weight of b
            float w2 = 1.0f - w0 - w1;                                                       // weight of c
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
            float z = w0 * a.z + w1 * b.z + w2 * c.z;
            size_t di = (size_t)y * (size_t)f.w + (size_t)x;
            if (z >= f.depth[di]) continue;
            float iw = w0 * a.invW + w1 * b.invW + w2 * c.invW;
            if (iw <= 0) continue;
            float k = 1.0f / iw;
            auto I = [&](float pa, float pb, float pc) { return (w0 * pa + w1 * pb + w2 * pc) * k; };
            Vec3 p{I(a.wx, b.wx, c.wx), I(a.wy, b.wy, c.wy), I(a.wz, b.wz, c.wz)};
            Vec3 n = s.smooth ? normalize(Vec3{I(a.nx, b.nx, c.nx), I(a.ny, b.ny, c.ny), I(a.nz, b.nz, c.nz)}) : faceNormal;
            // two sided: the side that faces the viewer is lit
            if (dot(n, eye - p) < 0) n = n * -1.0f;
            Vec3 base = s.color;
            if (s.useTexture && tex && tex->w > 0 && tex->h > 0) {
                float u = I(a.u, b.u, c.u), v = I(a.v, b.v, c.v);
                float tu = s.tm[0] * u + s.tm[1] * v + s.tm[2], tv = s.tm[3] * u + s.tm[4] * v + s.tm[5];
                float alpha;
                Vec3 t = sampleTexture(*tex, tu, tv, alpha);
                if (alpha < 0.5f) continue;   // a cut-out (transparent texel)
                base = {base.x * t.x, base.y * t.y, base.z * t.z};
            }
            f.depth[di] = z;
            Vec3 col = shade(base, n, p, eye, s, lights);
            uint8_t* px4 = rgba + di * 4;
            px4[0] = (uint8_t)std::min(255.0f, std::max(0.0f, col.x * 255.0f + 0.5f));
            px4[1] = (uint8_t)std::min(255.0f, std::max(0.0f, col.y * 255.0f + 0.5f));
            px4[2] = (uint8_t)std::min(255.0f, std::max(0.0f, col.z * 255.0f + 0.5f));
            px4[3] = 255;
        }
    }
}

/// Draws one mesh into the frame. `lights` are in world space.
inline void drawMesh(uint8_t* rgba, Frame& f, const CameraParams& cam, const Mesh& mesh, const ObjectParams& obj, const Surface& surf, const Texture* tex, const std::vector<Light>& lights) {
    if (f.w <= 0 || f.h <= 0 || mesh.idx.size() < 3) return;
    // model = T(pos) * R * S * T(-pivot)
    Mat4 model = mul(translation(obj.pos), mul(rotationXYZ(obj.rx, obj.ry, obj.rz), mul(scaling(obj.scale), translation(obj.pivot * -1.0f))));
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
    Mat4 normalM = mul(rotationXYZ(obj.rx, obj.ry, obj.rz), scaling(invS));

    size_t vertexCount = mesh.pos.size() / 3;
    std::vector<ClipVertex> cv(vertexCount);
    for (size_t i = 0; i < vertexCount; i++) {
        Vec3 wp = transformPoint(model, Vec3{mesh.pos[i * 3], mesh.pos[i * 3 + 1], mesh.pos[i * 3 + 2]});
        Vec3 wn = normalize(transformDir(normalM, Vec3{mesh.nrm[i * 3], mesh.nrm[i * 3 + 1], mesh.nrm[i * 3 + 2]}));
        ClipVertex& c = cv[i];
        c.wx = wp.x; c.wy = wp.y; c.wz = wp.z; c.nx = wn.x; c.ny = wn.y; c.nz = wn.z;
        c.u = mesh.uv[i * 2]; c.v = mesh.uv[i * 2 + 1];
        c.x = vp.at(0, 0) * wp.x + vp.at(0, 1) * wp.y + vp.at(0, 2) * wp.z + vp.at(0, 3);
        c.y = vp.at(1, 0) * wp.x + vp.at(1, 1) * wp.y + vp.at(1, 2) * wp.z + vp.at(1, 3);
        c.z = vp.at(2, 0) * wp.x + vp.at(2, 1) * wp.y + vp.at(2, 2) * wp.z + vp.at(2, 3);
        c.w = vp.at(3, 0) * wp.x + vp.at(3, 1) * wp.y + vp.at(3, 2) * wp.z + vp.at(3, 3);
    }
    auto toScreen = [&](const ClipVertex& c) {
        ScreenVertex s;
        float iw = 1.0f / c.w;
        s.sx = (c.x * iw * 0.5f + 0.5f) * f.w;
        s.sy = (1.0f - (c.y * iw * 0.5f + 0.5f)) * f.h;
        s.z = c.z * iw;
        s.invW = iw;
        s.wx = c.wx * iw; s.wy = c.wy * iw; s.wz = c.wz * iw;
        s.nx = c.nx * iw; s.ny = c.ny * iw; s.nz = c.nz * iw;
        s.u = c.u * iw; s.v = c.v * iw;
        return s;
    };
    for (size_t t = 0; t + 2 < mesh.idx.size(); t += 3) {
        uint32_t i0 = mesh.idx[t], i1 = mesh.idx[t + 1], i2 = mesh.idx[t + 2];
        if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount) continue;
        // clip against the near plane (z >= -w), the other planes are handled by the pixel loop
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
        // the face normal in the world (for flat shading and for the facing test)
        Vec3 wa{cv[i0].wx, cv[i0].wy, cv[i0].wz}, wb{cv[i1].wx, cv[i1].wy, cv[i1].wz}, wc{cv[i2].wx, cv[i2].wy, cv[i2].wz};
        Vec3 fn = normalize(cross(wb - wa, wc - wa));
        if (!surf.twoSided && dot(fn, cam.pos - wa) <= 0) continue;   // back face
        ScreenVertex sv[4];
        for (int i = 0; i < count; i++) sv[i] = toScreen(poly[i]);
        for (int i = 1; i + 1 < count; i++) rasterTriangle(rgba, f, sv[0], sv[i], sv[i + 1], surf, tex, cam.pos, lights, fn);
    }
}


// ---------------------------------------------------------------------------------------------------------------------
// Meshes: the templates (a unit size, centred on the origin) and the Wavefront OBJ reader
// ---------------------------------------------------------------------------------------------------------------------
enum TemplateKind { TemplateBox = 0, TemplateSphere = 1, TemplateCylinder = 2, TemplateCone = 3, TemplatePlane = 4 };

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
