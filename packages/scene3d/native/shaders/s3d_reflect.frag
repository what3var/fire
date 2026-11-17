#version 450
#extension GL_GOOGLE_include_directive : require
// fire scene3d, hardware backend: the reflections (Object.Reflectivity) - s3d_reflectTask of the software renderer in GLSL. A reflecting object is drawn again after the opaque
// objects (its own pixels only: the depth test LESS_OR_EQUAL against the picture's depth); the ray from the eye is mirrored at its normal and followed through the depth of
// the picture, and what it meets in the picture before the reflections (a copy of it) is mixed in by the reflectivity times a fresnel (the blending: alpha = that amount).
#include "s3d_common.glsl"
#include "s3d_object.glsl"
#include "s3d_surface.glsl"

layout(set = 3, binding = 0) uniform sampler2D depthMap;   // the depth of the picture (read texel by texel)
layout(set = 3, binding = 1) uniform sampler2D picture;    // the picture before the reflections

layout(location = 0) out vec4 outColor;

const int STEPS = 16;            // S3D_REFLECT_STEPS
const int REFINE = 5;            // S3D_REFLECT_REFINE
const float DISTANCE = 80.0;     // S3D_REFLECT_DISTANCE

// the depth of the picture as the software renderer keeps it (NDC z; 1e30: nothing there)
float ndcAt(ivec2 at) {
    float d = texelFetch(depthMap, at, 0).r;
    return d >= 1.0 ? 1e30 : d * 2.0 - 1.0;
}

void main() {
    vec3 n, base;
    float alpha;
    vec2 dux, duy;
    surfaceOf(n, base, alpha, dux, duy);
    float k = ob.glow.z;
    float w = fr.screen.x, h = fr.screen.y, tfx = fr.screen.z, tfy = fr.screen.w;
    int iw = int(w), ih = int(h);
    float nearP = fr.fog.z, farP = fr.fog.w, zA = nearP - farP, zB = farP + nearP, zC = 2.0 * farP * nearP, kA = zA / zC, kB = zB / zC;   // 1 / distance = z * kA + kB
    float ptx = 2.0 / (w * tfx), pty = 2.0 / (h * tfy), hw = 0.5 * w * tfx, hh = 0.5 * h * tfy;
    ivec2 at = ivec2(gl_FragCoord.xy);
    float x = float(at.x), y = float(at.y);
    if (k <= 0.0 || n.y < 0.5) discard;   // (only surfaces that face up: more than 30 degrees)
    vec3 nv = normalize(mat3(fr.view) * n);
    float z0 = ndcAt(at);
    if (z0 >= 1e29) discard;
    float d = zC / (z0 * zA + zB);
    if (d <= 0.0) discard;
    // the point in camera space (at the middle of its pixel), the view ray and its mirror image
    vec3 P = vec3((x + 0.5 - 0.5 * w) * ptx * d, (0.5 * h - y - 0.5) * pty * d, -d);
    float vl = length(P);
    float vn = dot(P, nv) / vl;
    float cosv = -vn;
    if (cosv <= 0.0) discard;
    vec3 R = P / vl - nv * (2.0 * vn);
    float c = 1.0 - cosv, c2 = c * c;
    float fres = 0.2 + 0.8 * c2 * c2 * c;
    float amount = k * fres;
    // the end of the ray: DISTANCE on, not behind the near plane
    float tEnd = DISTANCE;
    if (R.z > 1e-6) { float t = (-nearP * 1.01 - P.z) / R.z; if (t < tEnd) tEnd = t; }
    if (tEnd <= 0.0) discard;
    vec3 Q = P + R * tEnd;
    float iq = 1.0 / -Q.z;
    float sxA = x + 0.5, syA = y + 0.5;
    float sxB = Q.x * iq * hw + 0.5 * w, syB = 0.5 * h - Q.y * iq * hh;
    float izA = 1.0 / d, izB = iq;   // 1 / distance goes evenly over the screen
    izA *= 1.0 - 0.002 * cosv;       // (the start a little off the surface: it does not find itself)
    float dxs = sxB - sxA, dys = syB - syA;
    // only the part on the screen is followed
    float tOut = 1.0;
    if (dxs > 1e-6) tOut = min(tOut, (w - sxA) / dxs); else if (dxs < -1e-6) tOut = min(tOut, -sxA / dxs);
    if (dys > 1e-6) tOut = min(tOut, (h - syA) / dys); else if (dys < -1e-6) tOut = min(tOut, -syA / dys);
    float len = sqrt(dxs * dxs + dys * dys) * tOut;
    int steps = min(int(len / 3.0) + 1, STEPS);
    float dt = tOut / float(steps);
    float sPrev = 0.0, hit = -1.0, behind = -1.0;
    bool found = false;
    for (int s = 1; s <= steps && !found; s++) {
        float t = float(s) * dt, sx = sxA + dxs * t, sy = syA + dys * t;
        int ix = int(sx), iy = int(sy);
        if (sx < 0.0 || sy < 0.0 || ix >= iw || iy >= ih) break;
        float z = ndcAt(ivec2(ix, iy));
        float iray = izA + (izB - izA) * t, iscene = z * kA + kB;
        if (z < 1e29 && iray <= iscene) {
            float ray = 1.0 / iray, scene = 1.0 / iscene, prevRay = 1.0 / (izA + (izB - izA) * sPrev);
            if (ray - scene <= max(ray - prevRay, 0.0) + 0.05 + 0.02 * scene) {
                // behind what is drawn there: halve the last step a few times
                float lo = sPrev, hi = t;
                for (int r = 0; r < REFINE; r++) {
                    float m = (lo + hi) * 0.5, mx = sxA + dxs * m, my = syA + dys * m;
                    int jx = int(mx), jy = int(my);
                    if (mx < 0.0 || my < 0.0 || jx >= iw || jy >= ih) { lo = m; continue; }
                    float mz = ndcAt(ivec2(jx, jy));
                    if (mz < 1e29 && izA + (izB - izA) * m <= mz * kA + kB) hi = m; else lo = m;
                }
                hit = hi;
                found = true;
            }
            else if (behind < 0.0) behind = t;
        }
        sPrev = t;
    }
    // it only went behind something thicker than the step: that thing, rather than a hole
    if (!found && behind >= 0.0) { hit = behind; found = true; }
    vec3 col = fr.sky.rgb;
    if (found) {
        float hx = sxA + dxs * hit, hy = syA + dys * hit;
        ivec2 q = ivec2(clamp(int(floor(hx)), 0, iw - 1), clamp(int(floor(hy)), 0, ih - 1));
        // near the edges of the picture the found colour goes over into the sky
        float ex = min(hx, w - hx) / (0.08 * w), ey = min(hy, h - hy) / (0.08 * h);
        float fade = clamp(min(ex, ey), 0.0, 1.0);
        col = mix(fr.sky.rgb, texelFetch(picture, q, 0).rgb, fade);
    }
    outColor = vec4(col, amount);
}
