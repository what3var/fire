#version 450
#extension GL_GOOGLE_include_directive : require
// fire scene3d, hardware backend: the fog (Scene.Fog) over the finished picture - s3d_fxApply of the software renderer: from `start` on the picture goes into the fog colour,
// from `end` on it is all fog, and so is what no object covers - unless the picture has a backdrop (Object.Background, drawn without depth: the sky stays). The distance is the one of the depth of the picture (the opaque objects: a transparent one is fogged by
// what is behind it, as in the software renderer). Drawn over the picture with blending (alpha = how much fog).
#include "s3d_common.glsl"

layout(set = 3, binding = 0) uniform sampler2D depthMap;   // the depth of the picture (read texel by texel)

layout(location = 0) out vec4 outColor;

void main() {
    float d = texelFetch(depthMap, ivec2(gl_FragCoord.xy), 0).r;
    float nearP = fr.fog.z, farP = fr.fog.w, start = fr.fog.x, end = fr.fog.y;
    float k = fr.fogColor.a != 0.0 ? 0.0 : 1.0;
    if (d < 1.0) {
        // the distance as the software renderer has it from its depth (NDC z)
        float dist = 2.0 * farP * nearP / ((d * 2.0 - 1.0) * (nearP - farP) + farP + nearP);
        if (dist > 0.0) k = dist <= start ? 0.0 : dist >= end ? 1.0 : (dist - start) / (end - start);
    }
    if (k <= 0.0) discard;
    outColor = vec4(fr.fogColor.rgb, k);
}
