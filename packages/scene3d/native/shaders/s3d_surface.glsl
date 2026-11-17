// fire scene3d, hardware backend: the surface at a pixel - its normal (smooth, two sided, the bump map), its base colour and alpha (texture, cut-outs) - as s3d_rasterTri
// computes them. Shared by the colour of a pixel (s3d_main.frag) and the reflections (s3d_reflect.frag), which need the same normal.
layout(set = 1, binding = 0) uniform sampler2D tex;                // the colours (repeat, bilinear)
layout(set = 1, binding = 1) uniform sampler2D bumpMap;            // the heights

layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;

// Where a texture of `size` texels is read at uv with its filter (mode): 0 bilinear (the sampler), 1 the nearest texel (the middle of the texel, read bilinear, is the texel),
// 2 sharp bilinear (within a texel its value, only the last screen pixel before its edge blends into the next).
vec2 filtered(vec2 uv, vec2 size, float mode, vec2 dux, vec2 duy) {
    if (mode < 0.5) return uv;
    vec2 t = uv * size;
    if (mode < 1.5) t = floor(t) + 0.5;
    else {
        vec2 seam = floor(t + 0.5);
        vec2 d = max((abs(dux) + abs(duy)) * size, vec2(1e-6));
        t = seam + clamp((t - seam) / min(d, vec2(1.0)), -0.5, 0.5);
    }
    return t / size;
}

// The surface at this pixel (a cut-out texel is discarded). dux, duy: the derivatives of the texture coordinates (for the filters).
void surfaceOf(out vec3 n, out vec3 base, out float alpha, out vec2 dux, out vec2 duy) {
    vec3 p = vWorld;
    vec3 eye = fr.eye.xyz;
    // the derivatives first (before anything leaves the pixel out): the face normal and the directions of u and v on the surface
    vec3 dpx = dFdx(p), dpy = dFdy(p);
    dux = dFdx(vUv); duy = dFdy(vUv);
    // the normal of the face as its corners turn (counter clockwise seen from the front): towards the eye on a front face, away from it on a back face
    vec3 fn = normalize(cross(dpx, dpy));
    if ((dot(fn, eye - p) < 0.0) == gl_FrontFacing) fn = -fn;
    n = fn;
    if (ob.flags.x != 0) {
        // smooth: the interpolated normal; none (all corners without one) - the face's
        float l2 = dot(vNormal, vNormal);
        if (l2 > 1e-12) n = vNormal * inversesqrt(l2);
    }
    if (ob.flags.y != 0 && dot(n, eye - p) < 0.0) n = -n;
    base = ob.color.rgb;
    alpha = ob.color.a;
    if (ob.flags2.y != 0) {
        // the filter of the texture (ob.bump.w)
        vec4 tc = textureLod(tex, filtered(vUv, vec2(textureSize(tex, 0)), ob.bump.w, dux, duy), 0.0);
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
}
