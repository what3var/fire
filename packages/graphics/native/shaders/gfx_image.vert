#version 450
// fire graphics, GPU backend: a picture per instance - a glyph of the atlas or a blit of a framebuffer - over the pixels `inQuad` (clipped already).
layout(push_constant) uniform Target { vec2 size; } target;
layout(location = 0) in vec4 inQuad;   // x, y, w, h: the pixels that are drawn
layout(location = 1) in ivec4 inDst;   // dx, dy, dw, dh: where the picture goes (dw/dh < 0: mirrored)
layout(location = 2) in ivec4 inSrc;   // sx, sy, sw, sh: the part of the texture
layout(location = 3) in ivec4 inMode;  // mode (0 glyph, 1 copy, 2 transparent, 3 blend), unused
layout(location = 4) in vec4 inColor;  // the colour of a glyph
layout(location = 0) out vec4 vColor;
layout(location = 1) flat out ivec4 vDst;
layout(location = 2) flat out ivec4 vSrc;
layout(location = 3) flat out ivec4 vMode;

const vec2 corners[6] = vec2[](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0));

void main() {
    vec2 p = inQuad.xy + corners[gl_VertexIndex] * inQuad.zw;
    gl_Position = vec4(p / target.size * 2.0 - 1.0, 0.0, 1.0);
    vColor = inColor;
    vDst = inDst;
    vSrc = inSrc;
    vMode = inMode;
}
