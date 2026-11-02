// fire scene3d, hardware backend: the uniforms of the shaders. Keep in step with the structs in s3d_gpu_vk.h (S3dGpuFrameU, S3dGpuObjectU).
#define MAX_LIGHTS 16
#define MAX_SETS 16
#define MAX_MAPS 32

struct Light {
    ivec4 info;      // kind (1 directional, 2 point), layer mask, shadow set (-1: none)
    vec4 color;      // colour times intensity
    vec4 pos;        // position, 1/range
    vec4 toLight;    // directional: the unit direction towards the light
};
struct Map {
    mat4 view;       // world -> the view of the light (looking down -z)
    vec4 p;          // ortho: u = x*sx + ox, v = y*sy + oy  (sx, sy, ox, oy)
    vec4 q;          // f (perspective), res, ortho, layer
    vec4 r;          // ortho: the depth range (dmin, 1/(dmax-dmin)); perspective: far
};

layout(std140, row_major, set = 0, binding = 0) uniform FrameU {
    mat4 vp;
    vec4 eye;
    vec4 ambient;
    ivec4 counts;
    Light lights[MAX_LIGHTS];
    ivec4 sets[MAX_SETS];
    Map maps[MAX_MAPS];
} fr;
