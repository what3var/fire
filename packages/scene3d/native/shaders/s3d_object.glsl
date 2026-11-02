// fire scene3d, hardware backend: the uniforms of one object (S3dGpuObjectU in s3d_gpu_vk.h).
layout(std140, row_major, set = 0, binding = 2) uniform ObjectU {
    mat4 model;
    mat4 normalM;
    vec4 color;      // rgb, opacity
    vec4 emissive;   // rgb, cutoff
    vec4 tm0;        // a, b, tx, specular
    vec4 tm1;        // c, d, ty, shininess
    ivec4 flags;     // smooth, two sided, lit, receive shadows
    ivec4 flags2;    // layers, texture, bump map, blended
    vec4 bump;       // strength, 1/width, 1/height
} ob;
