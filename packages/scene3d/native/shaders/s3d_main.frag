#version 450
#extension GL_GOOGLE_include_directive : require
// fire scene3d, hardware backend: the colour of a pixel - s3d_rasterTri and s3d_shadowFactor of the software renderer (s3d_engine.h), computed the same way.
#include "s3d_common.glsl"
#include "s3d_object.glsl"

layout(set = 0, binding = 1) uniform sampler2DArray shadowMaps;   // the distance to the light (R32F), read texel by texel
layout(set = 1, binding = 0) uniform sampler2D tex;                // the colours (repeat, bilinear)
layout(set = 1, binding = 1) uniform sampler2D bumpMap;            // the heights

layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
layout(location = 0) out vec4 outColor;

bool shadowProject(Map m, vec3 p, out float u, out float v, out float d) {
    vec3 q = (m.view * vec4(p, 1.0)).xyz;
    d = -q.z;
    u = 0.0; v = 0.0;
    if (m.q.z != 0.0) { u = q.x * m.p.x + m.p.z; v = 1.0 - (q.y * m.p.y + m.p.w); return true; }
    if (d <= 1e-6) return false;
    float k = m.q.x * 0.5 / d;
    u = q.x * k + 0.5; v = 1.0 - (q.y * k + 0.5);
    return true;
}

// How much of the light reaches p (0 = in the shadow, 1 = lit): the depth in the shadow map, compared at the four texels around the point and weighted bilinearly (a soft edge).
float shadowFactor(int setIndex, vec3 lightPos, bool directional, vec3 p, vec3 n) {
    int mi = fr.sets[setIndex].x;
    if (mi < 0) return 1.0;
    if (!directional) {
        vec3 dd = p - lightPos;
        float ax = abs(dd.x), ay = abs(dd.y), az = abs(dd.z);
        mi += ax >= ay && ax >= az ? (dd.x >= 0.0 ? 0 : 1) : ay >= az ? (dd.y >= 0.0 ? 2 : 3) : (dd.z >= 0.0 ? 4 : 5);
    }
    Map m = fr.maps[mi];
    float res = m.q.y;
    float u, v, depth, texel;
    if (m.q.z != 0.0) texel = 1.0 / (m.p.x * res);
    else {
        if (!shadowProject(m, p, u, v, depth)) return 1.0;
        texel = 2.0 * depth / (m.q.x * res);
    }
    // move the point along its normal by about a texel: a surface does not shadow itself
    vec3 q = p + n * (texel * 1.5);
    if (!shadowProject(m, q, u, v, depth)) return 1.0;
    float bias = texel * 1.0 + 0.002;
    if (u < 0.0 || v < 0.0 || u >= 1.0 || v >= 1.0) return 1.0;
    float fx = u * res - 0.5, fy = v * res - 0.5;
    int x0 = int(floor(fx)), y0 = int(floor(fy));
    float tx = fx - float(x0), ty = fy - float(y0);
    int last = int(res) - 1;
    int x1 = min(last, x0 + 1), y1 = min(last, y0 + 1);
    x0 = max(0, x0); y0 = max(0, y0);
    int layer = int(m.q.w);
    float limit = depth - bias;
    float s00 = limit <= texelFetch(shadowMaps, ivec3(x0, y0, layer), 0).r ? 1.0 : 0.0;
    float s10 = limit <= texelFetch(shadowMaps, ivec3(x1, y0, layer), 0).r ? 1.0 : 0.0;
    float s01 = limit <= texelFetch(shadowMaps, ivec3(x0, y1, layer), 0).r ? 1.0 : 0.0;
    float s11 = limit <= texelFetch(shadowMaps, ivec3(x1, y1, layer), 0).r ? 1.0 : 0.0;
    return (s00 * (1.0 - tx) + s10 * tx) * (1.0 - ty) + (s01 * (1.0 - tx) + s11 * tx) * ty;
}

void main() {
    vec3 p = vWorld;
    vec3 eye = fr.eye.xyz;
    // the derivatives first (before anything leaves the pixel out): the face normal and the directions of u and v on the surface
    vec3 dpx = dFdx(p), dpy = dFdy(p);
    vec2 dux = dFdx(vUv), duy = dFdy(vUv);
    // the normal of the face as its corners turn (counter clockwise seen from the front): towards the eye on a front face, away from it on a back face
    vec3 fn = normalize(cross(dpx, dpy));
    if ((dot(fn, eye - p) < 0.0) == gl_FrontFacing) fn = -fn;
    vec3 n = fn;
    if (ob.flags.x != 0) {
        // smooth: the interpolated normal; none (all corners without one) - the face's
        float l2 = dot(vNormal, vNormal);
        if (l2 > 1e-12) n = vNormal * inversesqrt(l2);
    }
    if (ob.flags.y != 0 && dot(n, eye - p) < 0.0) n = -n;
    vec3 base = ob.color.rgb;
    float alpha = ob.color.a;
    if (ob.flags2.y != 0) {
        vec4 tc = textureLod(tex, vUv, 0.0);
        if (ob.emissive.w > 0.0 && tc.a < ob.emissive.w) discard;
        base *= tc.rgb;
        alpha *= tc.a;
        if (ob.flags2.z != 0) {
            // the directions of increasing u and v (from the derivatives: the same as the software renderer's per face) and the slope of the heights over one texel
            vec3 tangent = vec3(1.0, 0.0, 0.0), bitangent = vec3(0.0, 0.0, 1.0);
            float det = dux.x * duy.y - duy.x * dux.y;
            if (det != 0.0) {
                float r = 1.0 / det;
                tangent = normalize((dpx * duy.y - dpy * dux.y) * r);
                bitangent = normalize((dpy * dux.x - dpx * duy.x) * r);
            }
            float du = 0.5 * ob.bump.y, dv = 0.5 * ob.bump.z;
            float hu = (textureLod(bumpMap, vUv + vec2(du, 0.0), 0.0).r - textureLod(bumpMap, vUv - vec2(du, 0.0), 0.0).r) * ob.bump.x;
            float hv = (textureLod(bumpMap, vUv + vec2(0.0, dv), 0.0).r - textureLod(bumpMap, vUv - vec2(0.0, dv), 0.0).r) * ob.bump.x;
            n = normalize(n - tangent * hu - bitangent * hv);
        }
    }
    vec3 col;
    if (ob.flags.z == 0) col = base + ob.emissive.rgb;
    else {
        vec3 diffuse = fr.ambient.rgb, spec = vec3(0.0), viewDir = vec3(0.0);
        bool haveView = false;
        for (int i = 0; i < fr.counts.x; i++) {
            Light L = fr.lights[i];
            if ((L.info.y & ob.flags2.x) == 0) continue;
            vec3 toLight;
            float atten = 1.0;
            if (L.info.x == 1) toLight = L.toLight.xyz;
            else {
                vec3 d = L.pos.xyz - p;
                float dist = sqrt(dot(d, d));
                toLight = dist > 1e-12 ? d * (1.0 / dist) : vec3(0.0, 1.0, 0.0);
                float q = dist * L.pos.w;
                atten = 1.0 / (1.0 + q * q);
            }
            float nl = dot(n, toLight);
            if (nl <= 0.0) continue;
            float shadow = 1.0;
            if (L.info.z >= 0 && ob.flags.w != 0) shadow = shadowFactor(L.info.z, L.pos.xyz, L.info.x == 1, p, n);
            if (shadow <= 0.0) continue;
            float w = atten * shadow;
            diffuse += L.color.rgb * (nl * w);
            if (ob.tm0.w > 0.0) {
                if (!haveView) { viewDir = normalize(eye - p); haveView = true; }
                vec3 h = normalize(toLight + viewDir);
                float nh = dot(n, h);
                if (nh > 0.0) spec += L.color.rgb * (w * ob.tm0.w * pow(nh, ob.tm1.w));
            }
        }
        col = base * diffuse + spec + ob.emissive.rgb;
    }
    // (an opaque object writes alpha 1, a transparent one is mixed by its alpha and leaves the alpha of the picture as it is)
    outColor = vec4(col, ob.flags2.w != 0 ? alpha : 1.0);
}
