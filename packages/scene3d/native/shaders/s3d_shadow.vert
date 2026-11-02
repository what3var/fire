#version 450
#extension GL_GOOGLE_include_directive : require
// fire scene3d, hardware backend: a caster seen from a light - s3d_renderShadowMap of the software renderer. The map holds the distance along the view of the light (R32F).
#include "s3d_common.glsl"

layout(location = 0) in vec3 inPos;
layout(push_constant, row_major) uniform Push { mat4 model; int map; } pc;
layout(location = 0) out float vDepth;

void main() {
    Map m = mb.maps[pc.map];   // (the viewport is the map's place in the atlas)
    vec3 q = (m.view * (pc.model * vec4(inPos, 1.0))).xyz;
    float d = -q.z;
    if (m.q.z != 0.0) {
        // orthographic: u = x*sx + ox, v = 1 - (y*sy + oy) on the map; the depth range of the casters
        float u = q.x * m.p.x + m.p.z, v = 1.0 - (q.y * m.p.y + m.p.w);
        gl_Position = vec4(u * 2.0 - 1.0, v * 2.0 - 1.0, clamp((d - m.r.x) * m.r.y, 0.0, 1.0), 1.0);
    } else {
        // perspective: u = x*f/(2d) + 0.5; cut at 0.05 (as the software renderer does) and far beyond the casters
        float f = m.q.x, nearP = 0.05, farP = m.r.z;
        gl_Position = vec4(q.x * f, -q.y * f, (d - nearP) * farP / (farP - nearP), d);
    }
    vDepth = d;
}
