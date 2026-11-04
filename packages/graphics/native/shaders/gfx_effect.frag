#version 450
// The passes of fire_gfx_fxBlurMaterial, in whole numbers as the software computes them:
//   mode 0  smaller: the average of f x f pixels of the source (within region x0, y0, x1, y1), floor(sum / n)
//   mode 1  a box blur of radius p.x along x (mode 2: along y): (sum of 2r+1 values, the edge repeated, + div/2) / div
//   mode 3  back into the framebuffer: bilinear (16.16, 8-bit fractions), saturation, tint, grain, the light rim; alpha = the coverage of the rounded rectangle
// Push constants: region = bx0, by0, bx1, by1 (the source area); box = x, y, w, h (the rectangle); p = corner radius, blur radius, kind, rim light;
// q = tint (RGBA), saturation (/256), tw, th (the size of the small picture).
layout(set = 0, binding = 0) uniform sampler2D picture;
layout(push_constant) uniform Pass {
    vec2 size; int mode; int f;
    ivec4 rect; ivec4 region; ivec4 box; ivec4 p; ivec4 q;
} pass;
layout(location = 0) out vec4 outColor;

ivec4 texel(ivec2 c) { return ivec4(round(texelFetch(picture, c, 0) * 255.0)); }

float distanceTo(vec2 pt, vec4 box, float r) {
    vec2 h = box.zw * 0.5;
    r = clamp(r, 0.0, min(h.x, h.y));
    vec2 q = abs(pt - (box.xy + h)) - (h - r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// fire_gfx_fxSample: the value at (fx, fy) in 16.16, the edge repeated
ivec3 sampleAt(int fx, int fy, int w, int h) {
    if (fx < 0) fx = 0;
    if (fy < 0) fy = 0;
    int x0 = fx >> 16, y0 = fy >> 16;
    if (x0 >= w - 1) { x0 = w - 1; fx = x0 << 16; }
    if (y0 >= h - 1) { y0 = h - 1; fy = y0 << 16; }
    int x1 = min(w - 1, x0 + 1), y1 = min(h - 1, y0 + 1);
    uint ax = (uint(fx) >> 8) & 255u, ay = (uint(fy) >> 8) & 255u;
    uvec3 a = uvec3(texel(ivec2(x0, y0)).rgb), b = uvec3(texel(ivec2(x1, y0)).rgb), d = uvec3(texel(ivec2(x0, y1)).rgb), e = uvec3(texel(ivec2(x1, y1)).rgb);
    uvec3 top = a * (256u - ax) + b * ax, bottom = d * (256u - ax) + e * ax;
    return ivec3((top * (256u - ay) + bottom * ay + 32768u) >> 16);
}

int grain(int x, int y) {
    uint h = uint(x & 63) * 0x9E3779B1u ^ uint(y & 63) * 0x85EBCA77u;
    h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
    return int(h % 7u) - 3;
}

void main() {
    ivec2 pt = ivec2(floor(gl_FragCoord.xy));
    int f = pass.f;
    if (pass.mode == 0) {
        ivec4 sum = ivec4(0);
        int n = 0;
        for (int sy = pass.region.y + pt.y * f; sy < min(pass.region.w, pass.region.y + pt.y * f + f); sy++)
            for (int sx = pass.region.x + pt.x * f; sx < min(pass.region.z, pass.region.x + pt.x * f + f); sx++) { sum += texel(ivec2(sx, sy)); n++; }
        outColor = vec4(vec3(sum.rgb / max(n, 1)) / 255.0, 1.0);
        return;
    }
    if (pass.mode == 1 || pass.mode == 2) {
        int r = pass.p.y, div = 2 * r + 1;
        int n = pass.mode == 1 ? pass.q.z : pass.q.w;
        int at = pass.mode == 1 ? pt.x : pt.y;
        ivec3 sum = ivec3(0);
        for (int k = at - r; k <= at + r; k++) {
            int c = clamp(k, 0, n - 1);
            sum += texel(pass.mode == 1 ? ivec2(c, pt.y) : ivec2(pt.x, c)).rgb;
        }
        outColor = vec4(vec3((sum + div / 2) / div) / 255.0, 1.0);
        return;
    }
    // mode 3: into the framebuffer
    vec4 box = vec4(pass.box);
    float corner = float(pass.p.x);
    float dist = distanceTo(vec2(pt) + 0.5, box, corner);
    float c = 0.5 - dist;
    int cover = c <= 0.0 ? 0 : c >= 1.0 ? 255 : int(c * 255.0 + 0.5);
    if (cover == 0) discard;
    int sx = pt.x, sy = pt.y, light = 0, rim = pass.p.w;
    if (rim > 0) {
        float d = -dist;
        if (d < 2.0) {
            light = d < 1.0 ? rim : rim / 2;
            sx += float(pt.x) < box.x + box.z * 0.5 ? 1 : -1;
            sy += float(pt.y) < box.y + box.w * 0.5 ? 1 : -1;
        }
    }
    ivec3 v = sampleAt(((sx - pass.region.x) * 2 + 1) * 32768 / f - 32768, ((sy - pass.region.y) * 2 + 1) * 32768 / f - 32768, pass.q.z, pass.q.w);
    int l = (v.r * 77 + v.g * 150 + v.b * 29) >> 8;
    int sat = pass.q.y;
    ivec3 s = clamp(ivec3(l) + (((v - ivec3(l)) * sat) >> 8), 0, 255);
    uint tint = uint(pass.q.x), ta = tint >> 24, inv = 255u - ta;
    int g = pass.p.z == 2 ? grain(pt.x, pt.y) : 0;
    ivec3 o = ivec3((uvec3(s) * inv + uvec3(tint & 255u, (tint >> 8) & 255u, (tint >> 16) & 255u) * ta + 127u) / 255u) + g;
    if (light > 0) o += ((255 - o) * light) >> 8;
    o = clamp(o, 0, 255);
    outColor = vec4(vec3(o) / 255.0, float(cover) / 255.0);
}
