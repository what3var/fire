// fire scene3d, hardware backend: the data of a picture that the shaders share. Keep in step with the structs in s3d_gpu_vk.h (S3dGpuFrameU, S3dGpuLightU, S3dGpuMapU).
// The lights, the shadow maps and the lights of the tiles are storage buffers: as many as the scene has.

struct Light {
    ivec4 info;      // kind (1 directional, 2 point), layer mask, the first shadow map (-1: none; a point light has six from there)
    vec4 color;      // colour times intensity
    vec4 pos;        // position, 1/range
    vec4 toLight;    // directional: the unit direction towards the light; w (a point light): 1 / cutoff^2, 0: no end
};
struct Map {
    mat4 view;       // world -> the view of the light (looking down -z)
    vec4 p;          // ortho: u = x*sx + ox, v = y*sy + oy  (sx, sy, ox, oy)
    vec4 q;          // f (perspective), res, ortho
    vec4 r;          // ortho: the depth range (dmin, 1/(dmax-dmin)); perspective: far
    ivec4 t;         // the place of the map in the atlas: x, y, layer
};

layout(std140, row_major, set = 0, binding = 0) uniform FrameU {
    mat4 vp;
    vec4 eye;
    vec4 ambient;
    ivec4 counts;    // the lights; 1: the lights are culled per tile (Scene.LightCulling: TileBuf); the tiles across
    vec4 fog;        // start, end (not above start: no fog), the near and the far plane of the camera
    vec4 fogColor;   // rgb 0..1
    vec4 sky;        // rgb 0..1: what a reflection shows where it finds nothing (the background, or the fog)
    vec4 screen;     // width, height (pixels), 1/tan(fov/2) across, upwards
    mat4 view;       // world -> camera
} fr;
layout(std430, row_major, set = 0, binding = 3) readonly buffer LightBuf { Light lights[]; } lb;
layout(std430, row_major, set = 0, binding = 4) readonly buffer MapBuf { Map maps[]; } mb;
// The lights of each tile of 16 x 16 pixels (s3d_cullLights of the software renderer, the same lists): tile t lists t[t[t]] .. t[t[t + 1] - 1] (indices into the lights)
layout(std430, set = 0, binding = 5) readonly buffer TileBuf { int t[]; } tb;
