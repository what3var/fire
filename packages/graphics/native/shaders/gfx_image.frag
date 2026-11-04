#version 450
// The pixel of the picture as fire_gfx_blit picks it (nearest, in whole numbers: src = s + (2f + 1) * sw / (2 |dw|), f mirrored when dw < 0); a glyph is its coverage
// times the colour (as fire_gfx_ttfDrawGlyph: alpha * coverage).
layout(set = 0, binding = 0) uniform sampler2D picture;
layout(location = 0) in vec4 vColor;
layout(location = 1) flat in ivec4 vDst;
layout(location = 2) flat in ivec4 vSrc;
layout(location = 3) flat in ivec4 vMode;
layout(location = 0) out vec4 outColor;

float distanceTo(vec2 pt, vec4 box, float r) {
    vec2 h = box.zw * 0.5;
    r = clamp(r, 0.0, min(h.x, h.y));
    vec2 q = abs(pt - (box.xy + h)) - (h - r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// mode 4, a shadow (fire_gfx_fxShadow): the blurred mask (src: its place in the atlas, mw x mh) at 1/scale (mode.y), stretched bilinearly as fire_gfx_fxSample
// does; dst is the rectangle (its inside is left out), mode.z its radius, mode.w = pad + dy * 65536; the colour's alpha is the strongest opacity.
void shadow(ivec2 p) {
    int scale = vMode.y, pad = vMode.w & 65535, dy = vMode.w >> 16;
    int ox = vDst.x - pad, oy = vDst.y - pad + dy;
    uint cover = 0u;
    if (p.x >= vDst.x && p.x < vDst.x + vDst.z && p.y >= vDst.y && p.y < vDst.y + vDst.w) {
        float c = 0.5 - distanceTo(vec2(p) + 0.5, vec4(vDst), float(vMode.z));
        cover = c <= 0.0 ? 0u : c >= 1.0 ? 255u : uint(c * 255.0 + 0.5);
        if (cover >= 255u) discard;
    }
    int fx = ((p.x - ox) * 2 + 1) * 32768 / scale - 32768, fy = ((p.y - oy) * 2 + 1) * 32768 / scale - 32768;
    int w = vSrc.z, h = vSrc.w;
    if (fx < 0) fx = 0;
    if (fy < 0) fy = 0;
    int x0 = fx >> 16, y0 = fy >> 16;
    if (x0 >= w - 1) { x0 = w - 1; fx = x0 << 16; }
    if (y0 >= h - 1) { y0 = h - 1; fy = y0 << 16; }
    int x1 = min(w - 1, x0 + 1), y1 = min(h - 1, y0 + 1);
    uint ax = (uint(fx) >> 8) & 255u, ay = (uint(fy) >> 8) & 255u;
    uint a = uint(round(texelFetch(picture, vSrc.xy + ivec2(x0, y0), 0).r * 255.0)), b = uint(round(texelFetch(picture, vSrc.xy + ivec2(x1, y0), 0).r * 255.0));
    uint d = uint(round(texelFetch(picture, vSrc.xy + ivec2(x0, y1), 0).r * 255.0)), e = uint(round(texelFetch(picture, vSrc.xy + ivec2(x1, y1), 0).r * 255.0));
    uint top = a * (256u - ax) + b * ax, bottom = d * (256u - ax) + e * ax;
    uint v = (top * (256u - ay) + bottom * ay + 32768u) >> 16;
    uint alpha = uint(round(vColor.a * 255.0));
    uint c = (alpha * v * (255u - cover) + 32512u) / 65025u;
    if (c == 0u) discard;
    outColor = vec4(vColor.rgb, float(c) / 255.0);
}

void main() {
    ivec2 p = ivec2(floor(gl_FragCoord.xy));
    if (vMode.x == 4) { shadow(p); return; }
    int aw = abs(vDst.z), ah = abs(vDst.w);
    int i = p.x - vDst.x, j = p.y - vDst.y;
    int fx = vDst.z < 0 ? aw - 1 - i : i;
    int fy = vDst.w < 0 ? ah - 1 - j : j;
    ivec2 s = ivec2(vSrc.x + (2 * fx + 1) * vSrc.z / (2 * aw), vSrc.y + (2 * fy + 1) * vSrc.w / (2 * ah));
    ivec2 size = textureSize(picture, 0);
    if (s.x < 0 || s.y < 0 || s.x >= size.x || s.y >= size.y) discard;
    vec4 t = texelFetch(picture, s, 0);
    if (vMode.x == 0) {
        float a = vColor.a * t.r;
        if (a <= 0.0) discard;
        outColor = vec4(vColor.rgb, a);
        return;
    }
    if (vMode.x != 1 && t.a <= 0.0) discard;
    outColor = t;
}
