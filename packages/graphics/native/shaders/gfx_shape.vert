#version 450
// fire graphics, GPU backend (fire_gfx_gpu.h): one rounded rectangle per instance - the quad of pixels it covers, the box, its radius and stroke, its colour.
layout(push_constant) uniform Target { vec2 size; } target;
layout(location = 0) in vec4 inQuad;    // x, y, w, h: the pixels that are drawn (in pixels of the framebuffer)
layout(location = 1) in vec4 inBox;     // x, y, w, h: the rounded rectangle
layout(location = 2) in vec4 inParam;   // radius, stroke width (0: filled), copy (1: no blending - drawn from half coverage on), unused
layout(location = 3) in vec4 inColor;   // R, G, B, A
layout(location = 0) out vec4 vColor;
layout(location = 1) flat out vec4 vBox;
layout(location = 2) flat out vec4 vParam;

const vec2 corners[6] = vec2[](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0));

void main() {
    vec2 p = inQuad.xy + corners[gl_VertexIndex] * inQuad.zw;
    gl_Position = vec4(p / target.size * 2.0 - 1.0, 0.0, 1.0);
    vColor = inColor;
    vBox = inBox;
    vParam = inParam;
}
