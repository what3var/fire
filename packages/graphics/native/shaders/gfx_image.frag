#version 450
// The pixel of the picture as fire_gfx_blit picks it (nearest, in whole numbers: src = s + (2f + 1) * sw / (2 |dw|), f mirrored when dw < 0); a glyph is its coverage
// times the colour (as fire_gfx_ttfDrawGlyph: alpha * coverage).
layout(set = 0, binding = 0) uniform sampler2D picture;
layout(location = 0) in vec4 vColor;
layout(location = 1) flat in ivec4 vDst;
layout(location = 2) flat in ivec4 vSrc;
layout(location = 3) flat in ivec4 vMode;
layout(location = 0) out vec4 outColor;

void main() {
    ivec2 p = ivec2(floor(gl_FragCoord.xy));
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
