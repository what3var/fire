#version 450
#extension GL_GOOGLE_include_directive : require
// fire scene3d, hardware backend: the vertices of an object (as s3d_transformBatch does it).
#include "s3d_common.glsl"
#include "s3d_object.glsl"

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;

void main() {
    vec4 wp = ob.model * vec4(inPos, 1.0);
    vec3 n = mat3(ob.normalM) * inNormal;
    float l = length(n);
    vWorld = wp.xyz;
    // (no normal - an object scaled to 0 on an axis: the fragment shader takes the normal of the face, as the software renderer does then)
    vNormal = l > 1e-20 ? n / l : vec3(0.0);
    // the texture mapping is applied to the corners: the rest is interpolated
    vUv = vec2(dot(ob.tm0.xyz, vec3(inUv, 1.0)), dot(ob.tm1.xyz, vec3(inUv, 1.0)));
    gl_Position = fr.vp * wp;
}
