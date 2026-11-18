#version 450
#extension GL_GOOGLE_include_directive : require
// fire scene3d, hardware backend: the colour of a pixel - s3d_rasterTri and s3d_shadowFactor of the software renderer (s3d_engine.h), computed the same way.
#include "s3d_common.glsl"
#include "s3d_object.glsl"
#include "s3d_surface.glsl"

layout(set = 0, binding = 1) uniform sampler2DArray shadowMaps;   // the atlas of the shadow maps: the distance to the light (R32F), read texel by texel
layout(set = 2, binding = 0) uniform sampler2D glowMap;            // the emission map (Object.EmissiveTexture; the white texture when there is none)

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
float shadowFactor(int mi, vec3 lightPos, bool directional, vec3 p, vec3 n) {
    if (!directional) {
        vec3 dd = p - lightPos;
        float ax = abs(dd.x), ay = abs(dd.y), az = abs(dd.z);
        mi += ax >= ay && ax >= az ? (dd.x >= 0.0 ? 0 : 1) : ay >= az ? (dd.y >= 0.0 ? 2 : 3) : (dd.z >= 0.0 ? 4 : 5);
    }
    Map m = mb.maps[mi];
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
    ivec3 at = m.t.xyz;   // the corner of the map in the atlas
    float limit = depth - bias;
    float s00 = limit <= texelFetch(shadowMaps, at + ivec3(x0, y0, 0), 0).r ? 1.0 : 0.0;
    float s10 = limit <= texelFetch(shadowMaps, at + ivec3(x1, y0, 0), 0).r ? 1.0 : 0.0;
    float s01 = limit <= texelFetch(shadowMaps, at + ivec3(x0, y1, 0), 0).r ? 1.0 : 0.0;
    float s11 = limit <= texelFetch(shadowMaps, at + ivec3(x1, y1, 0), 0).r ? 1.0 : 0.0;
    return (s00 * (1.0 - tx) + s10 * tx) * (1.0 - ty) + (s01 * (1.0 - tx) + s11 * tx) * ty;
}

void main() {
    vec3 p = vWorld;
    vec3 eye = fr.eye.xyz;
    vec3 n, base;
    float alpha;
    vec2 dux, duy;
    surfaceOf(n, base, alpha, dux, duy);
    vec3 col;
    if (ob.flags.z == 0) col = base + ob.emissive.rgb;
    else {
        vec3 diffuse = fr.ambient.rgb, spec = vec3(0.0), viewDir = vec3(0.0);
        bool haveView = false;
        // the lights: all of them, or (Scene.LightCulling) those that can reach the tile of the pixel - the same lights in the same order
        int count = fr.counts.x, first = 0;
        bool culled = fr.counts.y != 0;
        if (culled) {
            ivec2 at = ivec2(gl_FragCoord.xy);
            int ti = (at.y >> 4) * fr.counts.z + (at.x >> 4);
            first = tb.t[ti];
            count = tb.t[ti + 1] - first;
        }
        for (int k = 0; k < count; k++) {
            Light L = lb.lights[culled ? tb.t[first + k] : k];
            if ((L.info.y & ob.flags2.x) == 0) continue;
            vec3 toLight;
            float atten = 1.0;
            if (L.info.x == 1) toLight = L.toLight.xyz;
            else {
                vec3 d = L.pos.xyz - p;
                float d2 = dot(d, d), r2 = 0.0;
                if (L.toLight.w > 0.0) { r2 = d2 * L.toLight.w; if (r2 >= 1.0) continue; }   // beyond its end (Light.Cutoff)
                float dist = sqrt(d2);
                toLight = dist > 1e-12 ? d * (1.0 / dist) : vec3(0.0, 1.0, 0.0);
                float q = dist * L.pos.w;
                atten = 1.0 / (1.0 + q * q);
                if (L.toLight.w > 0.0) { float wc = 1.0 - r2 * r2; atten *= wc * wc; }
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
    if (ob.glow.x != 0.0) {
        // the emission map: its colour times its alpha and the strength, added (at the texture coordinates of the texture, with its own filter)
        vec4 gc = textureLod(glowMap, filtered(vUv, vec2(textureSize(glowMap, 0)), ob.glow.y, dux, duy), 0.0);
        col += gc.rgb * (gc.a * ob.glow.x);
    }
    if (ob.flags2.w != 0) {
        // a transparent object is drawn in two draws (s3d_gpu_flushLocked): 2 its opaque texels (alpha 255 of 255 - S3D_OPAQUE_ALPHA of s3d_engine.h), which write the depth like an
        // opaque object, 1 the others, mixed in; each takes only its own pixels
        if ((ob.flags2.w == 2) != (alpha >= 1.0 - 0.5 / 255.0)) discard;
        // it is drawn after the fog of the picture: the fog of its own distance (the depth of the pixel, as the fog has it from the depth of the picture - s3d_fog.frag)
        if (fr.fog.y > fr.fog.x) {
            float nearP = fr.fog.z, farP = fr.fog.w;
            float dist = 2.0 * farP * nearP / ((gl_FragCoord.z * 2.0 - 1.0) * (nearP - farP) + farP + nearP);
            float k = dist <= 0.0 ? 1.0 : dist <= fr.fog.x ? 0.0 : dist >= fr.fog.y ? 1.0 : (dist - fr.fog.x) / (fr.fog.y - fr.fog.x);
            col = mix(col, fr.fogColor.rgb, k);
        }
    }
    // (an opaque object writes alpha 1, a transparent one is mixed by its alpha and leaves the alpha of the picture as it is)
    outColor = vec4(col, ob.flags2.w == 1 ? alpha : 1.0);
}
