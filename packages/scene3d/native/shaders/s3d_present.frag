#version 450
// fire scene3d, hardware backend, DirectDrawing: the camera's picture (from the GPU) with the framebuffer laid over it where its alpha is not 0 (what the program drew there).
layout(set = 0, binding = 0) uniform sampler2D scene;
layout(set = 0, binding = 1) uniform sampler2D overlay;
layout(push_constant) uniform Push { ivec4 flags; } pc;   // a picture of the camera, an overlay
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
void main() {
    vec3 c = pc.flags.x != 0 ? texture(scene, vUv).rgb : vec3(0.0);
    if (pc.flags.y != 0) {
        vec4 o = texture(overlay, vUv);
        c = mix(c, o.rgb, o.a);
    }
    outColor = vec4(c, 1.0);
}
