#version 450
// fire graphics, GPU backend: a pass of blur or material (fire_gfx_fx.h) over the rectangle `rect` of its target (a temporary picture, or the framebuffer).
layout(push_constant) uniform Pass {
    vec2 size; int mode; int f;
    ivec4 rect; ivec4 region; ivec4 box; ivec4 p; ivec4 q;
} pass;

const vec2 corners[6] = vec2[](vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0), vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0));

void main() {
    vec2 p = vec2(pass.rect.xy) + corners[gl_VertexIndex] * vec2(pass.rect.zw);
    gl_Position = vec4(p / pass.size * 2.0 - 1.0, 0.0, 1.0);
}
