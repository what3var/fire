/* fire scene3d: the hardware backend - the queue of s3d_engine.h drawn by the GPU through Vulkan (docs/SCENE3D.md, "Hardware").
 *
 * Optional at every level: Vulkan is not linked but loaded when the backend is first asked for (libvulkan.so.1, vulkan-1.dll, libvulkan.1.dylib or MoltenVK), the declarations it needs
 * come with the package (s3d_vk.h, made from the Khronos header: no SDK needed to build), and when anything is missing - no loader, no driver, no GPU, an error - `s3d_gpu_flush`
 * returns 0 and the software renderer draws the picture. Vulkan 1.0 core and nothing else (plus the portability extensions where they are there, for MoltenVK on macOS).
 *
 * What it draws is what the software renderer draws, computed per pixel the same way (shaders/s3d_main.frag is rasterTri in GLSL): Blinn-Phong with ambient, directional and point
 * lights, layers, textures with their mapping, bump maps, cut-outs, transparency (opaque objects first, then the transparent ones from far to near), two sided surfaces, billboards and
 * shadow maps (rendered on the GPU, compared and filtered like the software ones). The pixels are close to the software ones, not the same (the GPU rasterises and filters by its own
 * rules); the software renderer stays the reference.
 *
 * How: a picture is rendered into an image of the GPU and read back into the pixels of the camera (the framebuffer of the fire program), so nothing else changes - a window, a texture
 * made from a camera, a screenshot work as before. Meshes and textures are copied to the GPU when they are first drawn (and again when a texture gets another bump map); they are let go
 * with the mesh or texture. Every flush waits for the GPU: one picture at a time.
 *
 * Switches (environment): FIRE_S3D_GPU=0 never uses the GPU; FIRE_S3D_GPU=cpu also takes a Vulkan device that is a CPU (lavapipe, SwiftShader - slower than the software renderer, but
 * a GPU-less machine can test the backend with it). */
#ifndef FIRE_S3D_GPU_VK_H
#define FIRE_S3D_GPU_VK_H

#if defined(FIRE_S3D_NO_GPU) || defined(__EMSCRIPTEN__) || defined(ESP_PLATFORM) || !(defined(_WIN32) || defined(__linux__) || defined(__APPLE__) || defined(__FreeBSD__))

/* ---- no hardware backend on this target ---- */
static int s3d_gpu_flush(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, size_t lightCount, const S3dFlushSettings* st) {
    (void)rgba; (void)f; (void)cam; (void)lights; (void)lightCount; (void)st;
    return 0;
}
static void s3d_gpu_releaseMesh(S3dMesh* m) { (void)m; }
static void s3d_gpu_releaseTexture(S3dTexture* t) { (void)t; }
static void s3d_gpu_releaseFrame(S3dFrame* f) { (void)f; }
static void s3d_gpu_shutdown(void) {}
static const char* s3d_gpu_deviceName(void) { return ""; }
static const char* s3d_gpu_error(void) { return "This target has no hardware renderer."; }

#else

#ifdef FIRE_S3D_VK_SYSTEM_HEADERS
#include <vulkan/vulkan.h>       /* (to check s3d_vk.h against the real header) */
#else
#ifndef FIRE_S3D_VK_H
#include "s3d_vk.h"
#endif
#endif
#ifndef FIRE_S3D_SHADERS_H
#include "s3d_shaders.h"
#endif
#ifdef _WIN32
/* (windows.h: s3d_engine.h) */
#else
#include <dlfcn.h>
#endif

/* The functions the backend calls: loaded from the instance (I) and the device (D). */
#define S3D_VK_INSTANCE_FUNCTIONS(X) \
    X(vkDestroyInstance) X(vkEnumeratePhysicalDevices) X(vkGetPhysicalDeviceProperties) X(vkGetPhysicalDeviceQueueFamilyProperties) \
    X(vkGetPhysicalDeviceMemoryProperties) X(vkGetPhysicalDeviceFormatProperties) X(vkCreateDevice) X(vkGetDeviceProcAddr) X(vkEnumerateDeviceExtensionProperties)
#define S3D_VK_DEVICE_FUNCTIONS(X) \
    X(vkDestroyDevice) X(vkGetDeviceQueue) X(vkDeviceWaitIdle) X(vkCreateCommandPool) X(vkDestroyCommandPool) X(vkAllocateCommandBuffers) \
    X(vkBeginCommandBuffer) X(vkEndCommandBuffer) X(vkResetCommandBuffer) X(vkQueueSubmit) X(vkCreateFence) X(vkDestroyFence) X(vkWaitForFences) X(vkResetFences) \
    X(vkCreateBuffer) X(vkDestroyBuffer) X(vkGetBufferMemoryRequirements) X(vkAllocateMemory) X(vkFreeMemory) X(vkBindBufferMemory) X(vkMapMemory) X(vkUnmapMemory) \
    X(vkInvalidateMappedMemoryRanges) X(vkCreateImage) X(vkDestroyImage) X(vkGetImageMemoryRequirements) X(vkBindImageMemory) X(vkCreateImageView) X(vkDestroyImageView) \
    X(vkCreateSampler) X(vkDestroySampler) X(vkCreateShaderModule) X(vkDestroyShaderModule) X(vkCreateDescriptorSetLayout) X(vkDestroyDescriptorSetLayout) \
    X(vkCreatePipelineLayout) X(vkDestroyPipelineLayout) X(vkCreateDescriptorPool) X(vkDestroyDescriptorPool) X(vkAllocateDescriptorSets) X(vkFreeDescriptorSets) \
    X(vkUpdateDescriptorSets) X(vkCreateRenderPass) X(vkDestroyRenderPass) X(vkCreateFramebuffer) X(vkDestroyFramebuffer) X(vkCreateGraphicsPipelines) X(vkDestroyPipeline) \
    X(vkCmdBeginRenderPass) X(vkCmdEndRenderPass) X(vkCmdBindPipeline) X(vkCmdBindDescriptorSets) X(vkCmdBindVertexBuffers) X(vkCmdBindIndexBuffer) X(vkCmdDrawIndexed) \
    X(vkCmdSetViewport) X(vkCmdSetScissor) X(vkCmdPushConstants) X(vkCmdPipelineBarrier) X(vkCmdCopyBufferToImage) X(vkCmdCopyImageToBuffer) X(vkCmdCopyBuffer)

/* The uniform blocks of the shaders (std140; matrices row major, as the engine keeps them). Keep in step with shaders/s3d_common.glsl. */
#define S3D_GPU_MAX_LIGHTS 16
#define S3D_GPU_MAX_SETS 16
#define S3D_GPU_MAX_MAPS 32
typedef struct S3dGpuLightU { int32_t info[4]; float color[4]; float pos[4]; float toLight[4]; } S3dGpuLightU;   /* info: kind, mask, shadow set (-1: none) */
typedef struct S3dGpuMapU { float view[16]; float p[4]; float q[4]; float r[4]; } S3dGpuMapU;                  /* p: sx sy ox oy; q: f res ortho layer; r: dmin 1/range far */
typedef struct S3dGpuFrameU {
    float vp[16];
    float eye[4], ambient[4];
    int32_t counts[4];                         /* the lights */
    S3dGpuLightU lights[S3D_GPU_MAX_LIGHTS];
    int32_t sets[S3D_GPU_MAX_SETS][4];         /* the first map of a shadow set */
    S3dGpuMapU maps[S3D_GPU_MAX_MAPS];
} S3dGpuFrameU;
typedef struct S3dGpuObjectU {
    float model[16], normalM[16];
    float color[4];     /* rgb, opacity */
    float emissive[4];  /* rgb, cutoff */
    float tm0[4];       /* a b tx, specular */
    float tm1[4];       /* c d ty, shininess */
    int32_t flags[4];   /* smooth, two sided, lit, receive shadows */
    int32_t flags2[4];  /* layers, texture, bump map, blended */
    float bump[4];      /* strength, 1/width, 1/height */
} S3dGpuObjectU;
typedef struct S3dGpuShadowPush { float model[16]; int32_t map; } S3dGpuShadowPush;

typedef struct S3dVkBuffer { VkBuffer buf; VkDeviceMemory mem; VkDeviceSize size; void* map; } S3dVkBuffer;
typedef struct S3dVkImage { VkImage img; VkDeviceMemory mem; VkImageView view; } S3dVkImage;
typedef struct S3dGpuMesh { S3dVkBuffer vb, ib; uint32_t indexCount; } S3dGpuMesh;
typedef struct S3dGpuTex { S3dVkImage color, bump; int ownBump; VkDescriptorSet set; VkDescriptorPool pool; uint32_t version; } S3dGpuTex;
typedef struct S3dGpuFrame { int w, h; S3dVkImage color, depth; VkFramebuffer fb; S3dVkBuffer readback; } S3dGpuFrame;

#define S3D_GPU_PIPELINES 12   /* cull (none, back, front) x blend x depth write */

static struct {
    int state;                 /* 0: not tried yet, 1: ready, -1: there is none (error says why) */
    char error[256];
    char deviceName[256];
    void* lib;
    PFN_vkGetInstanceProcAddr vkGetInstanceProcAddr;
    PFN_vkCreateInstance vkCreateInstance;
    PFN_vkEnumerateInstanceExtensionProperties vkEnumerateInstanceExtensionProperties;
#define S3D_VK_DECLARE(name) PFN_##name name;
    S3D_VK_INSTANCE_FUNCTIONS(S3D_VK_DECLARE)
    S3D_VK_DEVICE_FUNCTIONS(S3D_VK_DECLARE)
#undef S3D_VK_DECLARE
    VkInstance instance;
    VkPhysicalDevice phys;
    VkDevice device;
    VkQueue queue;
    uint32_t family;
    VkPhysicalDeviceMemoryProperties memProps;
    VkDeviceSize uboAlign, objectStride;
    uint32_t maxImageDim, maxLayers;
    VkFormat depthFormat;
    VkCommandPool cmdPool;
    VkCommandBuffer cmd;
    VkFence fence;
    VkDescriptorSetLayout setFrame, setTex;
    VkPipelineLayout layoutMain, layoutShadow;
    VkDescriptorPool framePool;
    VkDescriptorSet frameSet;
    VkDescriptorPool* texPools; size_t texPoolN, texPoolCap;
    VkRenderPass passMain, passShadow;
    VkShaderModule mainVert, mainFrag, shadowVert, shadowFrag;
    VkPipeline pipes[S3D_GPU_PIPELINES];
    VkPipeline shadowPipe;
    VkSampler sampler, nearest;
    S3dGpuTex dummy;           /* a white texture with a flat bump map: for the objects without one */
    S3dVkImage flatBump;       /* the bump map of the textures without one (flat) */
    S3dVkBuffer frameUbo, objectUbo, staging;
    S3dVkImage shadowColor, shadowDepth;
    VkImageView* layerViews; VkFramebuffer* layerFbs;
    int shadowRes, shadowLayers;
    S3dGpuFrameU frameData;
    int* order; size_t orderCap;
} g_s3dVk;

static void s3d_gpu_fail(const char* text) {
    snprintf(g_s3dVk.error, sizeof g_s3dVk.error, "%s", text);
}
static int s3d_gpu_check(VkResult r, const char* what) {
    if (r == VK_SUCCESS) return 1;
    snprintf(g_s3dVk.error, sizeof g_s3dVk.error, "%s failed (VkResult %d).", what, (int)r);
    return 0;
}

/* ---- loading the library ---- */
static int s3d_gpu_loadLibrary(void) {
#ifdef _WIN32
    HMODULE lib = LoadLibraryA("vulkan-1.dll");
    if (lib == NULL) { s3d_gpu_fail("There is no Vulkan on this machine (vulkan-1.dll was not found)."); return 0; }
    g_s3dVk.lib = (void*)lib;
    g_s3dVk.vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)(void (*)(void))GetProcAddress(lib, "vkGetInstanceProcAddr");
#else
#ifdef __APPLE__
    static const char* names[] = {"libvulkan.1.dylib", "libvulkan.dylib", "libMoltenVK.dylib", "/usr/local/lib/libvulkan.1.dylib", "/opt/homebrew/lib/libvulkan.1.dylib", NULL};
#else
    static const char* names[] = {"libvulkan.so.1", "libvulkan.so", NULL};
#endif
    int i;
    void* sym;
    for (i = 0; names[i] != NULL && g_s3dVk.lib == NULL; i++) g_s3dVk.lib = dlopen(names[i], RTLD_NOW | RTLD_LOCAL);
    if (g_s3dVk.lib == NULL) { s3d_gpu_fail("There is no Vulkan on this machine (the Vulkan loader was not found)."); return 0; }
    sym = dlsym(g_s3dVk.lib, "vkGetInstanceProcAddr");
    memcpy(&g_s3dVk.vkGetInstanceProcAddr, &sym, sizeof sym);
#endif
    if (g_s3dVk.vkGetInstanceProcAddr == NULL) { s3d_gpu_fail("The Vulkan loader has no vkGetInstanceProcAddr."); return 0; }
    g_s3dVk.vkCreateInstance = (PFN_vkCreateInstance)g_s3dVk.vkGetInstanceProcAddr(NULL, "vkCreateInstance");
    g_s3dVk.vkEnumerateInstanceExtensionProperties = (PFN_vkEnumerateInstanceExtensionProperties)g_s3dVk.vkGetInstanceProcAddr(NULL, "vkEnumerateInstanceExtensionProperties");
    if (g_s3dVk.vkCreateInstance == NULL || g_s3dVk.vkEnumerateInstanceExtensionProperties == NULL) { s3d_gpu_fail("The Vulkan loader is incomplete."); return 0; }
    return 1;
}

static int s3d_gpu_hasExtension(const VkExtensionProperties* list, uint32_t count, const char* name) {
    uint32_t i;
    for (i = 0; i < count; i++) if (strcmp(list[i].extensionName, name) == 0) return 1;
    return 0;
}

static int s3d_gpu_createInstance(void) {
    VkApplicationInfo app;
    VkInstanceCreateInfo ci;
    uint32_t count = 0;
    VkExtensionProperties* exts = NULL;
    const char* enabled[1];
    VkResult r;
    memset(&app, 0, sizeof app);
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName = "fire scene3d";
    app.pEngineName = "fire scene3d";
    app.apiVersion = (uint32_t)1 << 22;   /* Vulkan 1.0 */
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ci.pApplicationInfo = &app;
    /* MoltenVK (macOS) is a "portability" implementation: it is only listed when the program says it can take one */
    if (g_s3dVk.vkEnumerateInstanceExtensionProperties(NULL, &count, NULL) == VK_SUCCESS && count > 0) {
        exts = (VkExtensionProperties*)malloc(sizeof(VkExtensionProperties) * count);
        if (exts && g_s3dVk.vkEnumerateInstanceExtensionProperties(NULL, &count, exts) == VK_SUCCESS && s3d_gpu_hasExtension(exts, count, "VK_KHR_portability_enumeration")) {
            enabled[0] = "VK_KHR_portability_enumeration";
            ci.enabledExtensionCount = 1;
            ci.ppEnabledExtensionNames = enabled;
            ci.flags = 0x00000001;   /* VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR */
        }
        free(exts);
    }
    r = g_s3dVk.vkCreateInstance(&ci, NULL, &g_s3dVk.instance);
    if (r != VK_SUCCESS) {
        if (r == VK_ERROR_INCOMPATIBLE_DRIVER) s3d_gpu_fail("There is no Vulkan driver on this machine.");
        else s3d_gpu_check(r, "vkCreateInstance");
        g_s3dVk.instance = NULL;
        return 0;
    }
#define S3D_VK_LOAD_I(name) \
    g_s3dVk.name = (PFN_##name)g_s3dVk.vkGetInstanceProcAddr(g_s3dVk.instance, #name); \
    if (g_s3dVk.name == NULL) { s3d_gpu_fail("The Vulkan loader lacks " #name "."); return 0; }
    S3D_VK_INSTANCE_FUNCTIONS(S3D_VK_LOAD_I)
#undef S3D_VK_LOAD_I
    return 1;
}

/* The device: a GPU (discrete before integrated before virtual) with a queue that draws; a CPU device only when asked for (FIRE_S3D_GPU=cpu). */
static int s3d_gpu_pickDevice(void) {
    uint32_t count = 0, i, best = 0, bestFamily = 0;
    int bestScore = 0;
    VkPhysicalDevice* list;
    const char* env = getenv("FIRE_S3D_GPU");
    int allowCpu = env != NULL && strcmp(env, "cpu") == 0;
    if (!s3d_gpu_check(g_s3dVk.vkEnumeratePhysicalDevices(g_s3dVk.instance, &count, NULL), "vkEnumeratePhysicalDevices")) return 0;
    if (count == 0) { s3d_gpu_fail("Vulkan finds no GPU on this machine."); return 0; }
    list = (VkPhysicalDevice*)malloc(sizeof(VkPhysicalDevice) * count);
    if (list == NULL) { s3d_gpu_fail("Out of memory."); return 0; }
    if (!s3d_gpu_check(g_s3dVk.vkEnumeratePhysicalDevices(g_s3dVk.instance, &count, list), "vkEnumeratePhysicalDevices")) { free(list); return 0; }
    for (i = 0; i < count; i++) {
        VkPhysicalDeviceProperties props;
        VkQueueFamilyProperties* fams;
        uint32_t famCount = 0, k;
        int score;
        g_s3dVk.vkGetPhysicalDeviceProperties(list[i], &props);
        switch (props.deviceType) {
            case VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU: score = 5; break;
            case VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU: score = 4; break;
            case VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU: score = 3; break;
            case VK_PHYSICAL_DEVICE_TYPE_CPU: score = allowCpu ? 1 : 0; break;
            default: score = 2; break;
        }
        if (score <= bestScore) continue;
        g_s3dVk.vkGetPhysicalDeviceQueueFamilyProperties(list[i], &famCount, NULL);
        fams = (VkQueueFamilyProperties*)malloc(sizeof(VkQueueFamilyProperties) * (famCount + 1));
        if (fams == NULL) continue;
        g_s3dVk.vkGetPhysicalDeviceQueueFamilyProperties(list[i], &famCount, fams);
        for (k = 0; k < famCount; k++)
            if ((fams[k].queueFlags & VK_QUEUE_GRAPHICS_BIT) && fams[k].queueCount > 0) { bestScore = score; best = i; bestFamily = k; break; }
        free(fams);
    }
    if (bestScore == 0) {
        free(list);
        s3d_gpu_fail(allowCpu ? "Vulkan finds no device that can draw." : "Vulkan finds no GPU on this machine (only a CPU device: FIRE_S3D_GPU=cpu takes it).");
        return 0;
    }
    g_s3dVk.phys = list[best];
    g_s3dVk.family = bestFamily;
    free(list);
    {
        VkPhysicalDeviceProperties props;
        g_s3dVk.vkGetPhysicalDeviceProperties(g_s3dVk.phys, &props);
        snprintf(g_s3dVk.deviceName, sizeof g_s3dVk.deviceName, "%s", props.deviceName);
        g_s3dVk.uboAlign = props.limits.minUniformBufferOffsetAlignment > 0 ? props.limits.minUniformBufferOffsetAlignment : 256;
        g_s3dVk.maxImageDim = props.limits.maxImageDimension2D;
        g_s3dVk.maxLayers = props.limits.maxImageArrayLayers;
    }
    g_s3dVk.objectStride = ((VkDeviceSize)sizeof(S3dGpuObjectU) + g_s3dVk.uboAlign - 1) / g_s3dVk.uboAlign * g_s3dVk.uboAlign;
    g_s3dVk.vkGetPhysicalDeviceMemoryProperties(g_s3dVk.phys, &g_s3dVk.memProps);
    return 1;
}

static int s3d_gpu_createDevice(void) {
    float priority = 1.0f;
    VkDeviceQueueCreateInfo qi;
    VkDeviceCreateInfo di;
    uint32_t count = 0;
    VkExtensionProperties* exts = NULL;
    const char* enabled[1];
    memset(&qi, 0, sizeof qi);
    qi.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qi.queueFamilyIndex = g_s3dVk.family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &priority;
    memset(&di, 0, sizeof di);
    di.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    di.queueCreateInfoCount = 1;
    di.pQueueCreateInfos = &qi;
    /* a portability implementation has to be told that the program knows it is one */
    if (g_s3dVk.vkEnumerateDeviceExtensionProperties(g_s3dVk.phys, NULL, &count, NULL) == VK_SUCCESS && count > 0) {
        exts = (VkExtensionProperties*)malloc(sizeof(VkExtensionProperties) * count);
        if (exts && g_s3dVk.vkEnumerateDeviceExtensionProperties(g_s3dVk.phys, NULL, &count, exts) == VK_SUCCESS && s3d_gpu_hasExtension(exts, count, "VK_KHR_portability_subset")) {
            enabled[0] = "VK_KHR_portability_subset";
            di.enabledExtensionCount = 1;
            di.ppEnabledExtensionNames = enabled;
        }
        free(exts);
    }
    if (!s3d_gpu_check(g_s3dVk.vkCreateDevice(g_s3dVk.phys, &di, NULL, &g_s3dVk.device), "vkCreateDevice")) { g_s3dVk.device = NULL; return 0; }
#define S3D_VK_LOAD_D(name) \
    g_s3dVk.name = (PFN_##name)g_s3dVk.vkGetDeviceProcAddr(g_s3dVk.device, #name); \
    if (g_s3dVk.name == NULL) { s3d_gpu_fail("The Vulkan driver lacks " #name "."); return 0; }
    S3D_VK_DEVICE_FUNCTIONS(S3D_VK_LOAD_D)
#undef S3D_VK_LOAD_D
    g_s3dVk.vkGetDeviceQueue(g_s3dVk.device, g_s3dVk.family, 0, &g_s3dVk.queue);
    return 1;
}

/* ---- memory, buffers, images ---- */
static int s3d_gpu_memoryType(uint32_t bits, VkMemoryPropertyFlags want, VkMemoryPropertyFlags prefer, uint32_t* index) {
    uint32_t i;
    for (i = 0; i < g_s3dVk.memProps.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (g_s3dVk.memProps.memoryTypes[i].propertyFlags & (want | prefer)) == (want | prefer)) { *index = i; return 1; }
    for (i = 0; i < g_s3dVk.memProps.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (g_s3dVk.memProps.memoryTypes[i].propertyFlags & want) == want) { *index = i; return 1; }
    return 0;
}

static void s3d_gpu_freeBuffer(S3dVkBuffer* b) {
    if (g_s3dVk.device == NULL) return;
    if (b->map) g_s3dVk.vkUnmapMemory(g_s3dVk.device, b->mem);
    if (b->buf) g_s3dVk.vkDestroyBuffer(g_s3dVk.device, b->buf, NULL);
    if (b->mem) g_s3dVk.vkFreeMemory(g_s3dVk.device, b->mem, NULL);
    memset(b, 0, sizeof *b);
}

/* A buffer; `host`: memory the CPU writes or reads (mapped for good), else memory of the GPU. */
static int s3d_gpu_buffer(S3dVkBuffer* b, VkDeviceSize size, VkBufferUsageFlags usage, int host, int cached) {
    VkBufferCreateInfo ci;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    uint32_t type;
    memset(b, 0, sizeof *b);
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    ci.size = size > 0 ? size : 4;
    ci.usage = usage;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (!s3d_gpu_check(g_s3dVk.vkCreateBuffer(g_s3dVk.device, &ci, NULL, &b->buf), "vkCreateBuffer")) { b->buf = 0; return 0; }
    g_s3dVk.vkGetBufferMemoryRequirements(g_s3dVk.device, b->buf, &req);
    if (!(host ? s3d_gpu_memoryType(req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, cached ? VK_MEMORY_PROPERTY_HOST_CACHED_BIT : 0, &type)
               : s3d_gpu_memoryType(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0, &type) || s3d_gpu_memoryType(req.memoryTypeBits, 0, 0, &type))) {
        s3d_gpu_fail("No fitting GPU memory for a buffer.");
        s3d_gpu_freeBuffer(b);
        return 0;
    }
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = type;
    if (!s3d_gpu_check(g_s3dVk.vkAllocateMemory(g_s3dVk.device, &ai, NULL, &b->mem), "vkAllocateMemory")) { b->mem = 0; s3d_gpu_freeBuffer(b); return 0; }
    if (!s3d_gpu_check(g_s3dVk.vkBindBufferMemory(g_s3dVk.device, b->buf, b->mem, 0), "vkBindBufferMemory")) { s3d_gpu_freeBuffer(b); return 0; }
    if (host && !s3d_gpu_check(g_s3dVk.vkMapMemory(g_s3dVk.device, b->mem, 0, VK_WHOLE_SIZE, 0, &b->map), "vkMapMemory")) { b->map = NULL; s3d_gpu_freeBuffer(b); return 0; }
    b->size = size;
    return 1;
}

/* A host buffer of at least `size` bytes (made again, bigger, when it is too small). */
static int s3d_gpu_hostBuffer(S3dVkBuffer* b, VkDeviceSize size, VkBufferUsageFlags usage) {
    VkDeviceSize grown;
    if (b->buf && b->size >= size) return 1;
    grown = b->size ? b->size : 4096;
    while (grown < size) grown *= 2;
    s3d_gpu_freeBuffer(b);
    return s3d_gpu_buffer(b, grown, usage, 1, 0);
}

static void s3d_gpu_freeImage(S3dVkImage* im) {
    if (g_s3dVk.device == NULL) return;
    if (im->view) g_s3dVk.vkDestroyImageView(g_s3dVk.device, im->view, NULL);
    if (im->img) g_s3dVk.vkDestroyImage(g_s3dVk.device, im->img, NULL);
    if (im->mem) g_s3dVk.vkFreeMemory(g_s3dVk.device, im->mem, NULL);
    memset(im, 0, sizeof *im);
}

/* An image of the GPU and its view (an array view when `layers` is above 1, or when the shaders read it as an array: the shadow maps). */
static int s3d_gpu_imageArray(S3dVkImage* im, uint32_t w, uint32_t h, uint32_t layers, VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect, int arrayView) {
    VkImageCreateInfo ci;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    VkImageViewCreateInfo vi;
    uint32_t type;
    memset(im, 0, sizeof *im);
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = format;
    ci.extent.width = w; ci.extent.height = h; ci.extent.depth = 1;
    ci.mipLevels = 1;
    ci.arrayLayers = layers;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = usage;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (!s3d_gpu_check(g_s3dVk.vkCreateImage(g_s3dVk.device, &ci, NULL, &im->img), "vkCreateImage")) { im->img = 0; return 0; }
    g_s3dVk.vkGetImageMemoryRequirements(g_s3dVk.device, im->img, &req);
    if (!s3d_gpu_memoryType(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0, &type) && !s3d_gpu_memoryType(req.memoryTypeBits, 0, 0, &type)) {
        s3d_gpu_fail("No fitting GPU memory for an image.");
        s3d_gpu_freeImage(im);
        return 0;
    }
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = type;
    if (!s3d_gpu_check(g_s3dVk.vkAllocateMemory(g_s3dVk.device, &ai, NULL, &im->mem), "vkAllocateMemory")) { im->mem = 0; s3d_gpu_freeImage(im); return 0; }
    if (!s3d_gpu_check(g_s3dVk.vkBindImageMemory(g_s3dVk.device, im->img, im->mem, 0), "vkBindImageMemory")) { s3d_gpu_freeImage(im); return 0; }
    memset(&vi, 0, sizeof vi);
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = im->img;
    vi.viewType = layers > 1 || arrayView ? VK_IMAGE_VIEW_TYPE_2D_ARRAY : VK_IMAGE_VIEW_TYPE_2D;
    vi.format = format;
    vi.subresourceRange.aspectMask = aspect;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = layers;
    if (!s3d_gpu_check(g_s3dVk.vkCreateImageView(g_s3dVk.device, &vi, NULL, &im->view), "vkCreateImageView")) { im->view = 0; s3d_gpu_freeImage(im); return 0; }
    return 1;
}
static int s3d_gpu_image(S3dVkImage* im, uint32_t w, uint32_t h, uint32_t layers, VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect) {
    return s3d_gpu_imageArray(im, w, h, layers, format, usage, aspect, 0);
}

/* ---- commands: one buffer, recorded, submitted and waited for ---- */
static int s3d_gpu_begin(void) {
    VkCommandBufferBeginInfo bi;
    if (!s3d_gpu_check(g_s3dVk.vkResetCommandBuffer(g_s3dVk.cmd, 0), "vkResetCommandBuffer")) return 0;
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    return s3d_gpu_check(g_s3dVk.vkBeginCommandBuffer(g_s3dVk.cmd, &bi), "vkBeginCommandBuffer");
}
static int s3d_gpu_submitAndWait(void) {
    VkSubmitInfo si;
    if (!s3d_gpu_check(g_s3dVk.vkEndCommandBuffer(g_s3dVk.cmd), "vkEndCommandBuffer")) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &g_s3dVk.cmd;
    if (!s3d_gpu_check(g_s3dVk.vkResetFences(g_s3dVk.device, 1, &g_s3dVk.fence), "vkResetFences")) return 0;
    if (!s3d_gpu_check(g_s3dVk.vkQueueSubmit(g_s3dVk.queue, 1, &si, g_s3dVk.fence), "vkQueueSubmit")) return 0;
    return s3d_gpu_check(g_s3dVk.vkWaitForFences(g_s3dVk.device, 1, &g_s3dVk.fence, VK_TRUE, UINT64_MAX), "vkWaitForFences");
}

static void s3d_gpu_imageBarrier(VkImage img, uint32_t layers, VkImageAspectFlags aspect, VkImageLayout from, VkImageLayout to, VkAccessFlags srcAccess, VkAccessFlags dstAccess,
                                 VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage) {
    VkImageMemoryBarrier b;
    memset(&b, 0, sizeof b);
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = srcAccess;
    b.dstAccessMask = dstAccess;
    b.oldLayout = from;
    b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img;
    b.subresourceRange.aspectMask = aspect;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = layers;
    g_s3dVk.vkCmdPipelineBarrier(g_s3dVk.cmd, srcStage, dstStage, 0, 0, NULL, 0, NULL, 1, &b);
}

/* Fills an image (one layer) with `bytes` and leaves it ready for the shaders. */
static int s3d_gpu_upload(VkImage img, uint32_t w, uint32_t h, const void* bytes, size_t size) {
    VkBufferImageCopy region;
    if (!s3d_gpu_hostBuffer(&g_s3dVk.staging, (VkDeviceSize)size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)) return 0;
    memcpy(g_s3dVk.staging.map, bytes, size);
    if (!s3d_gpu_begin()) return 0;
    s3d_gpu_imageBarrier(img, 1, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    memset(&region, 0, sizeof region);
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageExtent.width = w; region.imageExtent.height = h; region.imageExtent.depth = 1;
    g_s3dVk.vkCmdCopyBufferToImage(g_s3dVk.cmd, g_s3dVk.staging.buf, img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    s3d_gpu_imageBarrier(img, 1, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
    return s3d_gpu_submitAndWait();
}

/* A buffer of the GPU with `bytes` in it (vertices or indices). */
static int s3d_gpu_deviceBuffer(S3dVkBuffer* b, const void* bytes, size_t size, VkBufferUsageFlags usage, VkAccessFlags readAccess) {
    VkBufferCopy copy;
    VkMemoryBarrier mb;
    if (!s3d_gpu_hostBuffer(&g_s3dVk.staging, (VkDeviceSize)size, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)) return 0;
    if (!s3d_gpu_buffer(b, (VkDeviceSize)size, usage | VK_BUFFER_USAGE_TRANSFER_DST_BIT, 0, 0)) return 0;
    memcpy(g_s3dVk.staging.map, bytes, size);
    if (!s3d_gpu_begin()) { s3d_gpu_freeBuffer(b); return 0; }
    memset(&copy, 0, sizeof copy);
    copy.size = (VkDeviceSize)size;
    g_s3dVk.vkCmdCopyBuffer(g_s3dVk.cmd, g_s3dVk.staging.buf, b->buf, 1, &copy);
    memset(&mb, 0, sizeof mb);
    mb.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    mb.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    mb.dstAccessMask = readAccess;
    g_s3dVk.vkCmdPipelineBarrier(g_s3dVk.cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_VERTEX_INPUT_BIT, 0, 1, &mb, 0, NULL, 0, NULL);
    if (!s3d_gpu_submitAndWait()) { s3d_gpu_freeBuffer(b); return 0; }
    return 1;
}

/* ---- the fixed parts: layouts, render passes, shaders, samplers ---- */
static int s3d_gpu_shader(const uint32_t* code, size_t size, VkShaderModule* out) {
    VkShaderModuleCreateInfo ci;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    ci.codeSize = size;
    ci.pCode = code;
    return s3d_gpu_check(g_s3dVk.vkCreateShaderModule(g_s3dVk.device, &ci, NULL, out), "vkCreateShaderModule");
}

static int s3d_gpu_renderPass(VkFormat color, VkImageLayout colorFinal, VkPipelineStageFlags afterStage, VkAccessFlags afterAccess, VkRenderPass* out) {
    VkAttachmentDescription att[2];
    VkAttachmentReference colorRef, depthRef;
    VkSubpassDescription sub;
    VkSubpassDependency dep[2];
    VkRenderPassCreateInfo ci;
    memset(att, 0, sizeof att);
    att[0].format = color;
    att[0].samples = VK_SAMPLE_COUNT_1_BIT;
    att[0].loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    att[0].storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    att[0].stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    att[0].stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att[0].initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    att[0].finalLayout = colorFinal;
    att[1].format = g_s3dVk.depthFormat;
    att[1].samples = VK_SAMPLE_COUNT_1_BIT;
    att[1].loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    att[1].storeOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att[1].stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    att[1].stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att[1].initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    att[1].finalLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    colorRef.attachment = 0; colorRef.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    depthRef.attachment = 1; depthRef.layout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    memset(&sub, 0, sizeof sub);
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &colorRef;
    sub.pDepthStencilAttachment = &depthRef;
    memset(dep, 0, sizeof dep);
    /* before: whatever used the images last (the previous picture: a copy, or the shaders that read a shadow map) is done */
    dep[0].srcSubpass = VK_SUBPASS_EXTERNAL;
    dep[0].dstSubpass = 0;
    dep[0].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_LATE_FRAGMENT_TESTS_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT | VK_PIPELINE_STAGE_TRANSFER_BIT;
    dep[0].dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT;
    dep[0].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT;
    dep[0].dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT | VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_READ_BIT;
    /* after: the picture is read (copied to the CPU, or sampled as a shadow map) */
    dep[1].srcSubpass = 0;
    dep[1].dstSubpass = VK_SUBPASS_EXTERNAL;
    dep[1].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep[1].dstStageMask = afterStage;
    dep[1].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    dep[1].dstAccessMask = afterAccess;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
    ci.attachmentCount = 2;
    ci.pAttachments = att;
    ci.subpassCount = 1;
    ci.pSubpasses = &sub;
    ci.dependencyCount = 2;
    ci.pDependencies = dep;
    return s3d_gpu_check(g_s3dVk.vkCreateRenderPass(g_s3dVk.device, &ci, NULL, out), "vkCreateRenderPass");
}

static int s3d_gpu_sampler(VkFilter filter, VkSamplerAddressMode mode, VkSampler* out) {
    VkSamplerCreateInfo ci;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    ci.magFilter = filter;
    ci.minFilter = filter;
    ci.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    ci.addressModeU = mode; ci.addressModeV = mode; ci.addressModeW = mode;
    ci.maxLod = 0.0f;
    ci.borderColor = VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE;
    return s3d_gpu_check(g_s3dVk.vkCreateSampler(g_s3dVk.device, &ci, NULL, out), "vkCreateSampler");
}

static int s3d_gpu_layouts(void) {
    VkDescriptorSetLayoutBinding b[3];
    VkDescriptorSetLayoutCreateInfo li;
    VkPipelineLayoutCreateInfo pi;
    VkDescriptorSetLayout sets[2];
    VkPushConstantRange pc;
    VkDescriptorPoolSize sizes[3];
    VkDescriptorPoolCreateInfo dp;
    VkDescriptorSetAllocateInfo ai;
    /* set 0, the picture: the uniforms of the frame (lights, shadow maps, camera), the shadow maps, the uniforms of the object (dynamic offset) */
    memset(b, 0, sizeof b);
    b[0].binding = 0; b[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER; b[0].descriptorCount = 1; b[0].stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    b[1].binding = 1; b[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; b[1].descriptorCount = 1; b[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    b[2].binding = 2; b[2].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC; b[2].descriptorCount = 1; b[2].stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    memset(&li, 0, sizeof li);
    li.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    li.bindingCount = 3;
    li.pBindings = b;
    if (!s3d_gpu_check(g_s3dVk.vkCreateDescriptorSetLayout(g_s3dVk.device, &li, NULL, &g_s3dVk.setFrame), "vkCreateDescriptorSetLayout")) return 0;
    /* set 1, the texture: the colours and the bump map */
    memset(b, 0, sizeof b);
    b[0].binding = 0; b[0].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; b[0].descriptorCount = 1; b[0].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    b[1].binding = 1; b[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; b[1].descriptorCount = 1; b[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    li.bindingCount = 2;
    if (!s3d_gpu_check(g_s3dVk.vkCreateDescriptorSetLayout(g_s3dVk.device, &li, NULL, &g_s3dVk.setTex), "vkCreateDescriptorSetLayout")) return 0;
    sets[0] = g_s3dVk.setFrame; sets[1] = g_s3dVk.setTex;
    memset(&pi, 0, sizeof pi);
    pi.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pi.setLayoutCount = 2;
    pi.pSetLayouts = sets;
    if (!s3d_gpu_check(g_s3dVk.vkCreatePipelineLayout(g_s3dVk.device, &pi, NULL, &g_s3dVk.layoutMain), "vkCreatePipelineLayout")) return 0;
    /* the shadow maps: set 0 and the model matrix and the map as push constants */
    memset(&pc, 0, sizeof pc);
    pc.stageFlags = VK_SHADER_STAGE_VERTEX_BIT;
    pc.size = (uint32_t)sizeof(S3dGpuShadowPush);
    pi.setLayoutCount = 1;
    pi.pushConstantRangeCount = 1;
    pi.pPushConstantRanges = &pc;
    if (!s3d_gpu_check(g_s3dVk.vkCreatePipelineLayout(g_s3dVk.device, &pi, NULL, &g_s3dVk.layoutShadow), "vkCreatePipelineLayout")) return 0;
    /* the one set 0 */
    memset(sizes, 0, sizeof sizes);
    sizes[0].type = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER; sizes[0].descriptorCount = 1;
    sizes[1].type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; sizes[1].descriptorCount = 1;
    sizes[2].type = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC; sizes[2].descriptorCount = 1;
    memset(&dp, 0, sizeof dp);
    dp.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    dp.maxSets = 1;
    dp.poolSizeCount = 3;
    dp.pPoolSizes = sizes;
    if (!s3d_gpu_check(g_s3dVk.vkCreateDescriptorPool(g_s3dVk.device, &dp, NULL, &g_s3dVk.framePool), "vkCreateDescriptorPool")) return 0;
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    ai.descriptorPool = g_s3dVk.framePool;
    ai.descriptorSetCount = 1;
    ai.pSetLayouts = &g_s3dVk.setFrame;
    return s3d_gpu_check(g_s3dVk.vkAllocateDescriptorSets(g_s3dVk.device, &ai, &g_s3dVk.frameSet), "vkAllocateDescriptorSets");
}

/* Writes the buffers and the shadow maps into set 0 (after one of them was made again). */
static void s3d_gpu_writeFrameSet(void) {
    VkDescriptorBufferInfo frameInfo, objectInfo;
    VkDescriptorImageInfo shadowInfo;
    VkWriteDescriptorSet w[3];
    memset(w, 0, sizeof w);
    frameInfo.buffer = g_s3dVk.frameUbo.buf; frameInfo.offset = 0; frameInfo.range = sizeof(S3dGpuFrameU);
    objectInfo.buffer = g_s3dVk.objectUbo.buf; objectInfo.offset = 0; objectInfo.range = sizeof(S3dGpuObjectU);
    shadowInfo.sampler = g_s3dVk.nearest; shadowInfo.imageView = g_s3dVk.shadowColor.view; shadowInfo.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    w[0].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET; w[0].dstSet = g_s3dVk.frameSet; w[0].dstBinding = 0; w[0].descriptorCount = 1;
    w[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER; w[0].pBufferInfo = &frameInfo;
    w[1].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET; w[1].dstSet = g_s3dVk.frameSet; w[1].dstBinding = 1; w[1].descriptorCount = 1;
    w[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; w[1].pImageInfo = &shadowInfo;
    w[2].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET; w[2].dstSet = g_s3dVk.frameSet; w[2].dstBinding = 2; w[2].descriptorCount = 1;
    w[2].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC; w[2].pBufferInfo = &objectInfo;
    g_s3dVk.vkUpdateDescriptorSets(g_s3dVk.device, 3, w, 0, NULL);
}

/* A pipeline: the main one (cull: 0 none, 1 back faces, 2 front faces; blend; depth write), or the one of the shadow maps (shadow != 0). */
static VkPipeline s3d_gpu_pipeline(int shadow, int cull, int blend, int depthWrite) {
    VkPipeline* slot = shadow ? &g_s3dVk.shadowPipe : &g_s3dVk.pipes[cull * 4 + blend * 2 + depthWrite];
    VkPipelineShaderStageCreateInfo stages[2];
    VkVertexInputBindingDescription vb;
    VkVertexInputAttributeDescription va[3];
    VkPipelineVertexInputStateCreateInfo vi;
    VkPipelineInputAssemblyStateCreateInfo ia;
    VkPipelineViewportStateCreateInfo vp;
    VkPipelineRasterizationStateCreateInfo rs;
    VkPipelineMultisampleStateCreateInfo ms;
    VkPipelineDepthStencilStateCreateInfo ds;
    VkPipelineColorBlendAttachmentState cba;
    VkPipelineColorBlendStateCreateInfo cb;
    VkDynamicState dyn[2];
    VkPipelineDynamicStateCreateInfo dy;
    VkGraphicsPipelineCreateInfo ci;
    if (*slot) return *slot;
    memset(stages, 0, sizeof stages);
    stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO; stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT; stages[0].pName = "main";
    stages[0].module = shadow ? g_s3dVk.shadowVert : g_s3dVk.mainVert;
    stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO; stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].pName = "main";
    stages[1].module = shadow ? g_s3dVk.shadowFrag : g_s3dVk.mainFrag;
    /* a vertex: position, normal, texture coordinates */
    vb.binding = 0; vb.stride = 32; vb.inputRate = VK_VERTEX_INPUT_RATE_VERTEX;
    va[0].location = 0; va[0].binding = 0; va[0].format = VK_FORMAT_R32G32B32_SFLOAT; va[0].offset = 0;
    va[1].location = 1; va[1].binding = 0; va[1].format = VK_FORMAT_R32G32B32_SFLOAT; va[1].offset = 12;
    va[2].location = 2; va[2].binding = 0; va[2].format = VK_FORMAT_R32G32_SFLOAT; va[2].offset = 24;
    memset(&vi, 0, sizeof vi);
    vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    vi.vertexBindingDescriptionCount = 1; vi.pVertexBindingDescriptions = &vb;
    vi.vertexAttributeDescriptionCount = shadow ? 1 : 3; vi.pVertexAttributeDescriptions = va;
    memset(&ia, 0, sizeof ia);
    ia.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    memset(&vp, 0, sizeof vp);
    vp.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
    vp.viewportCount = 1; vp.scissorCount = 1;
    memset(&rs, 0, sizeof rs);
    rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = shadow || cull == 0 ? VK_CULL_MODE_NONE : cull == 1 ? VK_CULL_MODE_BACK_BIT : VK_CULL_MODE_FRONT_BIT;
    /* counter clockwise seen from outside, in a right handed world: after the projection (y down) that is still counter clockwise in Vulkan's sense */
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rs.lineWidth = 1.0f;
    memset(&ms, 0, sizeof ms);
    ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    memset(&ds, 0, sizeof ds);
    ds.sType = VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO;
    ds.depthTestEnable = VK_TRUE;
    ds.depthWriteEnable = shadow || depthWrite ? VK_TRUE : VK_FALSE;
    ds.depthCompareOp = VK_COMPARE_OP_LESS;
    memset(&cba, 0, sizeof cba);
    cba.colorWriteMask = shadow ? VK_COLOR_COMPONENT_R_BIT : (VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT);
    if (!shadow && blend) {
        /* the colour is mixed with what is behind; the alpha of the picture stays */
        cba.blendEnable = VK_TRUE;
        cba.srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA; cba.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA; cba.colorBlendOp = VK_BLEND_OP_ADD;
        cba.srcAlphaBlendFactor = VK_BLEND_FACTOR_ZERO; cba.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE; cba.alphaBlendOp = VK_BLEND_OP_ADD;
    }
    memset(&cb, 0, sizeof cb);
    cb.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
    cb.attachmentCount = 1; cb.pAttachments = &cba;
    dyn[0] = VK_DYNAMIC_STATE_VIEWPORT; dyn[1] = VK_DYNAMIC_STATE_SCISSOR;
    memset(&dy, 0, sizeof dy);
    dy.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
    dy.dynamicStateCount = 2; dy.pDynamicStates = dyn;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    ci.stageCount = 2; ci.pStages = stages;
    ci.pVertexInputState = &vi; ci.pInputAssemblyState = &ia; ci.pViewportState = &vp; ci.pRasterizationState = &rs;
    ci.pMultisampleState = &ms; ci.pDepthStencilState = &ds; ci.pColorBlendState = &cb; ci.pDynamicState = &dy;
    ci.layout = shadow ? g_s3dVk.layoutShadow : g_s3dVk.layoutMain;
    ci.renderPass = shadow ? g_s3dVk.passShadow : g_s3dVk.passMain;
    if (!s3d_gpu_check(g_s3dVk.vkCreateGraphicsPipelines(g_s3dVk.device, 0, 1, &ci, NULL, slot), "vkCreateGraphicsPipelines")) *slot = 0;
    return *slot;
}

/* ---- textures ---- */
static VkDescriptorSet s3d_gpu_textureSet(VkImageView color, VkImageView bump, VkDescriptorPool* poolOut) {
    VkDescriptorSetAllocateInfo ai;
    VkDescriptorSet set = 0;
    VkDescriptorImageInfo info[2];
    VkWriteDescriptorSet w[2];
    VkResult r = VK_ERROR_OUT_OF_POOL_MEMORY;
    size_t i;
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    ai.descriptorSetCount = 1;
    ai.pSetLayouts = &g_s3dVk.setTex;
    for (i = 0; i < g_s3dVk.texPoolN; i++) {
        ai.descriptorPool = g_s3dVk.texPools[i];
        r = g_s3dVk.vkAllocateDescriptorSets(g_s3dVk.device, &ai, &set);
        if (r == VK_SUCCESS) { *poolOut = g_s3dVk.texPools[i]; break; }
    }
    if (r != VK_SUCCESS) {
        /* a new pool of 256 sets */
        VkDescriptorPoolSize size;
        VkDescriptorPoolCreateInfo dp;
        VkDescriptorPool pool;
        size.type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; size.descriptorCount = 512;
        memset(&dp, 0, sizeof dp);
        dp.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
        dp.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
        dp.maxSets = 256;
        dp.poolSizeCount = 1;
        dp.pPoolSizes = &size;
        if (!S3D_GROW(g_s3dVk.texPools, g_s3dVk.texPoolCap, g_s3dVk.texPoolN + 1)) return 0;
        if (!s3d_gpu_check(g_s3dVk.vkCreateDescriptorPool(g_s3dVk.device, &dp, NULL, &pool), "vkCreateDescriptorPool")) return 0;
        g_s3dVk.texPools[g_s3dVk.texPoolN++] = pool;
        ai.descriptorPool = pool;
        if (!s3d_gpu_check(g_s3dVk.vkAllocateDescriptorSets(g_s3dVk.device, &ai, &set), "vkAllocateDescriptorSets")) return 0;
        *poolOut = pool;
    }
    info[0].sampler = g_s3dVk.sampler; info[0].imageView = color; info[0].imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    info[1].sampler = g_s3dVk.sampler; info[1].imageView = bump; info[1].imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    memset(w, 0, sizeof w);
    for (i = 0; i < 2; i++) {
        w[i].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET; w[i].dstSet = set; w[i].dstBinding = (uint32_t)i; w[i].descriptorCount = 1;
        w[i].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; w[i].pImageInfo = &info[i];
    }
    g_s3dVk.vkUpdateDescriptorSets(g_s3dVk.device, 2, w, 0, NULL);
    return set;
}

static void s3d_gpu_freeTex(S3dGpuTex* t) {
    if (t->set && t->pool) g_s3dVk.vkFreeDescriptorSets(g_s3dVk.device, t->pool, 1, &t->set);
    s3d_gpu_freeImage(&t->color);
    if (t->ownBump) s3d_gpu_freeImage(&t->bump);
    memset(t, 0, sizeof *t);
}

/* Copies a texture (and its bump map; without one it gets the flat one) to the GPU. */
static int s3d_gpu_makeTex(S3dGpuTex* t, int w, int h, const uint8_t* rgba, const uint8_t* bump, uint32_t version) {
    memset(t, 0, sizeof *t);
    if (!s3d_gpu_image(&t->color, (uint32_t)w, (uint32_t)h, 1, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT, VK_IMAGE_ASPECT_COLOR_BIT)) return 0;
    if (!s3d_gpu_upload(t->color.img, (uint32_t)w, (uint32_t)h, rgba, (size_t)w * (size_t)h * 4)) { s3d_gpu_freeTex(t); return 0; }
    if (bump != NULL) {
        t->ownBump = 1;
        if (!s3d_gpu_image(&t->bump, (uint32_t)w, (uint32_t)h, 1, VK_FORMAT_R8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT, VK_IMAGE_ASPECT_COLOR_BIT)
            || !s3d_gpu_upload(t->bump.img, (uint32_t)w, (uint32_t)h, bump, (size_t)w * (size_t)h)) { s3d_gpu_freeTex(t); return 0; }
    } else t->bump = g_s3dVk.flatBump;
    t->set = s3d_gpu_textureSet(t->color.view, t->bump.view, &t->pool);
    if (!t->set) { s3d_gpu_freeTex(t); return 0; }
    t->version = version;
    return 1;
}

static S3dGpuTex* s3d_gpu_texFor(S3dTexture* tex) {
    S3dGpuTex* g = (S3dGpuTex*)tex->gpu;
    if (g && g->version == tex->version) return g;
    if (g) { s3d_gpu_freeTex(g); free(g); tex->gpu = NULL; }
    if ((uint32_t)tex->w > g_s3dVk.maxImageDim || (uint32_t)tex->h > g_s3dVk.maxImageDim) { s3d_gpu_fail("A texture is bigger than the GPU takes."); return NULL; }
    g = (S3dGpuTex*)calloc(1, sizeof(S3dGpuTex));
    if (g == NULL) return NULL;
    if (!s3d_gpu_makeTex(g, tex->w, tex->h, tex->rgba, tex->bump, tex->version)) { free(g); return NULL; }
    tex->gpu = g;
    return g;
}

/* ---- meshes: a vertex is position, normal, texture coordinates (32 bytes) ---- */
static S3dGpuMesh* s3d_gpu_meshFor(S3dMesh* mesh) {
    S3dGpuMesh* g = (S3dGpuMesh*)mesh->gpu;
    size_t n = mesh->posN / 3, i;
    float* v;
    if (g) return g;
    g = (S3dGpuMesh*)calloc(1, sizeof(S3dGpuMesh));
    v = (float*)malloc(n * 8 * sizeof(float) + 4);
    if (g == NULL || v == NULL) { free(g); free(v); return NULL; }
    for (i = 0; i < n; i++) {
        v[i * 8 + 0] = mesh->pos[i * 3]; v[i * 8 + 1] = mesh->pos[i * 3 + 1]; v[i * 8 + 2] = mesh->pos[i * 3 + 2];
        v[i * 8 + 3] = mesh->nrm[i * 3]; v[i * 8 + 4] = mesh->nrm[i * 3 + 1]; v[i * 8 + 5] = mesh->nrm[i * 3 + 2];
        v[i * 8 + 6] = mesh->uv[i * 2]; v[i * 8 + 7] = mesh->uv[i * 2 + 1];
    }
    if (!s3d_gpu_deviceBuffer(&g->vb, v, n * 8 * sizeof(float), VK_BUFFER_USAGE_VERTEX_BUFFER_BIT, VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT)
        || !s3d_gpu_deviceBuffer(&g->ib, mesh->idx, (mesh->idxN / 3 * 3) * sizeof(uint32_t), VK_BUFFER_USAGE_INDEX_BUFFER_BIT, VK_ACCESS_INDEX_READ_BIT)) {
        s3d_gpu_freeBuffer(&g->vb);
        free(v); free(g);
        return NULL;
    }
    free(v);
    g->indexCount = (uint32_t)(mesh->idxN / 3 * 3);
    mesh->gpu = g;
    return g;
}

/* ---- the targets of a frame: the picture, its depth, the buffer it is read back into ---- */
static void s3d_gpu_freeFrameTargets(S3dGpuFrame* g) {
    if (g->fb) g_s3dVk.vkDestroyFramebuffer(g_s3dVk.device, g->fb, NULL);
    s3d_gpu_freeImage(&g->color);
    s3d_gpu_freeImage(&g->depth);
    s3d_gpu_freeBuffer(&g->readback);
    memset(g, 0, sizeof *g);
}

static int s3d_gpu_framebuffer(VkRenderPass pass, VkImageView color, VkImageView depth, uint32_t w, uint32_t h, VkFramebuffer* out) {
    VkImageView views[2];
    VkFramebufferCreateInfo ci;
    views[0] = color; views[1] = depth;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
    ci.renderPass = pass;
    ci.attachmentCount = 2;
    ci.pAttachments = views;
    ci.width = w; ci.height = h; ci.layers = 1;
    return s3d_gpu_check(g_s3dVk.vkCreateFramebuffer(g_s3dVk.device, &ci, NULL, out), "vkCreateFramebuffer");
}

static S3dGpuFrame* s3d_gpu_frameFor(S3dFrame* f) {
    S3dGpuFrame* g = (S3dGpuFrame*)f->gpu;
    if (g && g->w == f->w && g->h == f->h) return g;
    if (g) s3d_gpu_freeFrameTargets(g);
    else {
        g = (S3dGpuFrame*)calloc(1, sizeof(S3dGpuFrame));
        if (g == NULL) return NULL;
        f->gpu = g;
    }
    if (!s3d_gpu_image(&g->color, (uint32_t)f->w, (uint32_t)f->h, 1, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT, VK_IMAGE_ASPECT_COLOR_BIT)
        || !s3d_gpu_image(&g->depth, (uint32_t)f->w, (uint32_t)f->h, 1, g_s3dVk.depthFormat, VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT, VK_IMAGE_ASPECT_DEPTH_BIT)
        || !s3d_gpu_framebuffer(g_s3dVk.passMain, g->color.view, g->depth.view, (uint32_t)f->w, (uint32_t)f->h, &g->fb)
        || !s3d_gpu_buffer(&g->readback, (VkDeviceSize)f->w * (VkDeviceSize)f->h * 4, VK_BUFFER_USAGE_TRANSFER_DST_BIT, 1, 1)) {
        s3d_gpu_freeFrameTargets(g);
        return NULL;
    }
    g->w = f->w; g->h = f->h;
    return g;
}

/* ---- the shadow maps: one array of R32F layers (the distance to the light) and a depth buffer; a framebuffer per layer ---- */
static void s3d_gpu_freeShadows(void) {
    int i;
    for (i = 0; i < g_s3dVk.shadowLayers; i++) {
        if (g_s3dVk.layerFbs && g_s3dVk.layerFbs[i]) g_s3dVk.vkDestroyFramebuffer(g_s3dVk.device, g_s3dVk.layerFbs[i], NULL);
        if (g_s3dVk.layerViews && g_s3dVk.layerViews[i]) g_s3dVk.vkDestroyImageView(g_s3dVk.device, g_s3dVk.layerViews[i], NULL);
    }
    free(g_s3dVk.layerFbs); free(g_s3dVk.layerViews);
    g_s3dVk.layerFbs = NULL; g_s3dVk.layerViews = NULL;
    s3d_gpu_freeImage(&g_s3dVk.shadowColor);
    s3d_gpu_freeImage(&g_s3dVk.shadowDepth);
    g_s3dVk.shadowRes = 0; g_s3dVk.shadowLayers = 0;
}

static int s3d_gpu_shadows(int res, int layers) {
    int i;
    if (g_s3dVk.shadowRes == res && g_s3dVk.shadowLayers == layers) return 1;
    s3d_gpu_freeShadows();
    if (!s3d_gpu_imageArray(&g_s3dVk.shadowColor, (uint32_t)res, (uint32_t)res, (uint32_t)layers, VK_FORMAT_R32_SFLOAT, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT, VK_IMAGE_ASPECT_COLOR_BIT, 1)
        || !s3d_gpu_image(&g_s3dVk.shadowDepth, (uint32_t)res, (uint32_t)res, 1, g_s3dVk.depthFormat, VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT, VK_IMAGE_ASPECT_DEPTH_BIT)) {
        s3d_gpu_freeShadows();
        return 0;
    }
    g_s3dVk.layerViews = (VkImageView*)calloc((size_t)layers, sizeof(VkImageView));
    g_s3dVk.layerFbs = (VkFramebuffer*)calloc((size_t)layers, sizeof(VkFramebuffer));
    g_s3dVk.shadowRes = res; g_s3dVk.shadowLayers = layers;
    if (g_s3dVk.layerViews == NULL || g_s3dVk.layerFbs == NULL) { s3d_gpu_freeShadows(); return 0; }
    for (i = 0; i < layers; i++) {
        VkImageViewCreateInfo vi;
        memset(&vi, 0, sizeof vi);
        vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        vi.image = g_s3dVk.shadowColor.img;
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = VK_FORMAT_R32_SFLOAT;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        vi.subresourceRange.levelCount = 1;
        vi.subresourceRange.baseArrayLayer = (uint32_t)i;
        vi.subresourceRange.layerCount = 1;
        if (!s3d_gpu_check(g_s3dVk.vkCreateImageView(g_s3dVk.device, &vi, NULL, &g_s3dVk.layerViews[i]), "vkCreateImageView")
            || !s3d_gpu_framebuffer(g_s3dVk.passShadow, g_s3dVk.layerViews[i], g_s3dVk.shadowDepth.view, (uint32_t)res, (uint32_t)res, &g_s3dVk.layerFbs[i])) {
            s3d_gpu_freeShadows();
            return 0;
        }
    }
    /* every layer starts ready for the shaders (a layer that is not drawn in a picture is not read either, but it has to be in that layout) */
    if (!s3d_gpu_begin()) { s3d_gpu_freeShadows(); return 0; }
    s3d_gpu_imageBarrier(g_s3dVk.shadowColor.img, (uint32_t)layers, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, 0,
                         VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
    if (!s3d_gpu_submitAndWait()) { s3d_gpu_freeShadows(); return 0; }
    s3d_gpu_writeFrameSet();
    return 1;
}

/* ---- starting: everything that does not depend on a picture ---- */
static int s3d_gpu_start(void) {
    VkCommandPoolCreateInfo pi;
    VkCommandBufferAllocateInfo ai;
    VkFenceCreateInfo fi;
    VkFormatProperties fp;
    static const VkFormat depthFormats[3] = {VK_FORMAT_D32_SFLOAT, VK_FORMAT_X8_D24_UNORM_PACK32, VK_FORMAT_D16_UNORM};
    static const uint8_t white[4] = {255, 255, 255, 255};
    static const uint8_t flat[1] = {0};
    int i;
    const char* env = getenv("FIRE_S3D_GPU");
    if (env != NULL && strcmp(env, "0") == 0) { s3d_gpu_fail("The hardware renderer is switched off (FIRE_S3D_GPU=0)."); return 0; }
    if (!s3d_gpu_loadLibrary() || !s3d_gpu_createInstance() || !s3d_gpu_pickDevice() || !s3d_gpu_createDevice()) return 0;
    g_s3dVk.depthFormat = VK_FORMAT_UNDEFINED;
    for (i = 0; i < 3; i++) {
        g_s3dVk.vkGetPhysicalDeviceFormatProperties(g_s3dVk.phys, depthFormats[i], &fp);
        if (fp.optimalTilingFeatures & VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT) { g_s3dVk.depthFormat = depthFormats[i]; break; }
    }
    if (g_s3dVk.depthFormat == VK_FORMAT_UNDEFINED) { s3d_gpu_fail("The GPU has no depth format."); return 0; }
    memset(&pi, 0, sizeof pi);
    pi.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pi.queueFamilyIndex = g_s3dVk.family;
    if (!s3d_gpu_check(g_s3dVk.vkCreateCommandPool(g_s3dVk.device, &pi, NULL, &g_s3dVk.cmdPool), "vkCreateCommandPool")) return 0;
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    ai.commandPool = g_s3dVk.cmdPool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = 1;
    if (!s3d_gpu_check(g_s3dVk.vkAllocateCommandBuffers(g_s3dVk.device, &ai, &g_s3dVk.cmd), "vkAllocateCommandBuffers")) return 0;
    memset(&fi, 0, sizeof fi);
    fi.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    if (!s3d_gpu_check(g_s3dVk.vkCreateFence(g_s3dVk.device, &fi, NULL, &g_s3dVk.fence), "vkCreateFence")) return 0;
    if (!s3d_gpu_layouts()) return 0;
    if (!s3d_gpu_renderPass(VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_ACCESS_TRANSFER_READ_BIT, &g_s3dVk.passMain)) return 0;
    if (!s3d_gpu_renderPass(VK_FORMAT_R32_SFLOAT, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_ACCESS_SHADER_READ_BIT, &g_s3dVk.passShadow)) return 0;
    if (!s3d_gpu_shader(s3d_spv_main_vert, sizeof s3d_spv_main_vert, &g_s3dVk.mainVert) || !s3d_gpu_shader(s3d_spv_main_frag, sizeof s3d_spv_main_frag, &g_s3dVk.mainFrag)
        || !s3d_gpu_shader(s3d_spv_shadow_vert, sizeof s3d_spv_shadow_vert, &g_s3dVk.shadowVert) || !s3d_gpu_shader(s3d_spv_shadow_frag, sizeof s3d_spv_shadow_frag, &g_s3dVk.shadowFrag)) return 0;
    if (!s3d_gpu_sampler(VK_FILTER_LINEAR, VK_SAMPLER_ADDRESS_MODE_REPEAT, &g_s3dVk.sampler) || !s3d_gpu_sampler(VK_FILTER_NEAREST, VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE, &g_s3dVk.nearest)) return 0;
    if (!s3d_gpu_hostBuffer(&g_s3dVk.frameUbo, sizeof(S3dGpuFrameU), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT)
        || !s3d_gpu_hostBuffer(&g_s3dVk.objectUbo, g_s3dVk.objectStride * 16, VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT)) return 0;
    /* the texture of the objects without one: white, and flat */
    if (!s3d_gpu_image(&g_s3dVk.flatBump, 1, 1, 1, VK_FORMAT_R8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT, VK_IMAGE_ASPECT_COLOR_BIT)
        || !s3d_gpu_upload(g_s3dVk.flatBump.img, 1, 1, flat, 1)
        || !s3d_gpu_makeTex(&g_s3dVk.dummy, 1, 1, white, NULL, 0)) return 0;
    return s3d_gpu_shadows(64, 1);
}

/* Starts the backend the first time it is asked for: 1 when it is there. */
static int s3d_gpu_ready(void) {
    if (g_s3dVk.state == 0) g_s3dVk.state = s3d_gpu_start() ? 1 : -1;
    return g_s3dVk.state == 1;
}

/* ---- drawing ---- */
static void s3d_gpu_copyMat(float* out, const S3dMat4* m) { memcpy(out, m->m, sizeof(float) * 16); }

/* The world bounds of the vertices of a batch (the casters of shadows): the software renderer takes the same bounds from its transformed vertices. */
static void s3d_gpu_bounds(const S3dBatch* b, S3dVec3* lo, S3dVec3* hi) {
    size_t i, n = b->mesh->posN / 3;
    for (i = 0; i < n; i++) {
        S3dVec3 w = s3d_transformPoint(&b->model, s3d_v3(b->mesh->pos[i * 3], b->mesh->pos[i * 3 + 1], b->mesh->pos[i * 3 + 2]));
        *lo = s3d_v3(s3d_minf(lo->x, w.x), s3d_minf(lo->y, w.y), s3d_minf(lo->z, w.z));
        *hi = s3d_v3(s3d_maxf(hi->x, w.x), s3d_maxf(hi->y, w.y), s3d_maxf(hi->z, w.z));
    }
}

static void s3d_gpu_draw(const S3dBatch* b, size_t objectIndex, int cull, int blend, int depthWrite) {
    VkPipeline pipe = s3d_gpu_pipeline(0, cull, blend, depthWrite);
    const S3dGpuMesh* m = (const S3dGpuMesh*)b->mesh->gpu;
    const S3dGpuTex* t = b->tex ? (const S3dGpuTex*)b->tex->gpu : &g_s3dVk.dummy;
    VkDescriptorSet sets[2];
    uint32_t offset = (uint32_t)(objectIndex * g_s3dVk.objectStride);
    VkDeviceSize zero = 0;
    if (!pipe || !m || !t) return;
    sets[0] = g_s3dVk.frameSet; sets[1] = t->set;
    g_s3dVk.vkCmdBindPipeline(g_s3dVk.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe);
    g_s3dVk.vkCmdBindDescriptorSets(g_s3dVk.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_s3dVk.layoutMain, 0, 2, sets, 1, &offset);
    g_s3dVk.vkCmdBindVertexBuffers(g_s3dVk.cmd, 0, 1, &m->vb.buf, &zero);
    g_s3dVk.vkCmdBindIndexBuffer(g_s3dVk.cmd, m->ib.buf, 0, VK_INDEX_TYPE_UINT32);
    g_s3dVk.vkCmdDrawIndexed(g_s3dVk.cmd, m->indexCount, 1, 0, 0, 0);
}

static void s3d_gpu_viewport(uint32_t w, uint32_t h) {
    VkViewport vp;
    VkRect2D sc;
    vp.x = 0; vp.y = 0; vp.width = (float)w; vp.height = (float)h; vp.minDepth = 0; vp.maxDepth = 1;
    sc.offset.x = 0; sc.offset.y = 0; sc.extent.width = w; sc.extent.height = h;
    g_s3dVk.vkCmdSetViewport(g_s3dVk.cmd, 0, 1, &vp);
    g_s3dVk.vkCmdSetScissor(g_s3dVk.cmd, 0, 1, &sc);
}

/* Fills the uniforms of the picture: camera, lights, shadow maps. Returns the number of shadow maps to draw. */
static int s3d_gpu_frameUniforms(S3dFrame* f, const S3dCamera* cam, const S3dFlushSettings* st, int layerRes, S3dVec3 lo, S3dVec3 hi) {
    S3dGpuFrameU* u = &g_s3dVk.frameData;
    S3dContext* ctx = &f->ctx;
    /* the projection of the engine (OpenGL clip space) for Vulkan: y down, z from 0 to w */
    S3dMat4 vk = s3d_identity(), vp;
    int maps = 0, k, i;
    size_t li, n = 0;
    (void)st;
    S3D_AT(vk, 1, 1) = -1; S3D_AT(vk, 2, 2) = 0.5f; S3D_AT(vk, 2, 3) = 0.5f;
    vp = s3d_mul(vk, s3d_viewProjection(cam, f->w, f->h));
    memset(u, 0, sizeof *u);
    s3d_gpu_copyMat(u->vp, &vp);
    u->eye[0] = ctx->eye.x; u->eye[1] = ctx->eye.y; u->eye[2] = ctx->eye.z;
    u->ambient[0] = ctx->ambient.x; u->ambient[1] = ctx->ambient.y; u->ambient[2] = ctx->ambient.z;
    /* the shadow maps of the sets; a set that does not fit any more casts no shadow */
    for (k = 0; k < ctx->shadowN && k < S3D_GPU_MAX_SETS; k++) {
        const S3dShadowSet* set = &ctx->shadows[k];
        if (maps + set->count > S3D_GPU_MAX_MAPS || maps + set->count > g_s3dVk.shadowLayers) { u->sets[k][0] = -1; continue; }
        u->sets[k][0] = maps;
        for (i = 0; i < set->count; i++, maps++) {
            const S3dShadowMap* m = &set->maps[i];
            S3dGpuMapU* g = &u->maps[maps];
            int res = s3d_mini(m->res, layerRes);
            s3d_gpu_copyMat(g->view, &m->view);
            g->p[0] = m->sx; g->p[1] = m->sy; g->p[2] = m->ox; g->p[3] = m->oy;
            g->q[0] = m->f; g->q[1] = (float)res; g->q[2] = m->ortho ? 1.0f : 0.0f; g->q[3] = (float)maps;
            {
                /* the depth range of the casters in the view of the light (orthographic) or how far they are (perspective) */
                float dmin = 1e30f, dmax = -1e30f, farthest = 1.0f;
                int c;
                for (c = 0; c < 8; c++) {
                    S3dVec3 corner = s3d_v3((c & 1) ? hi.x : lo.x, (c & 2) ? hi.y : lo.y, (c & 4) ? hi.z : lo.z);
                    S3dVec3 q = s3d_transformPoint(&m->view, corner);
                    dmin = s3d_minf(dmin, -q.z); dmax = s3d_maxf(dmax, -q.z);
                    farthest = s3d_maxf(farthest, s3d_length(q));
                }
                dmin -= 0.01f + 0.01f * (dmax - dmin); dmax += 0.01f + 0.01f * (dmax - dmin);
                g->r[0] = dmin; g->r[1] = 1.0f / s3d_maxf(dmax - dmin, 1e-6f); g->r[2] = farthest * 1.5f + 1.0f;
            }
        }
    }
    for (li = 0; li < ctx->lightN && n < S3D_GPU_MAX_LIGHTS; li++) {
        const S3dPreparedLight* L = &ctx->lights[li];
        S3dGpuLightU* g = &u->lights[n++];
        g->info[0] = L->kind; g->info[1] = L->mask; g->info[2] = L->shadow >= 0 && L->shadow < S3D_GPU_MAX_SETS ? L->shadow : -1;
        g->color[0] = L->color.x; g->color[1] = L->color.y; g->color[2] = L->color.z;
        g->pos[0] = L->pos.x; g->pos[1] = L->pos.y; g->pos[2] = L->pos.z; g->pos[3] = L->invRange;
        g->toLight[0] = L->toLight.x; g->toLight[1] = L->toLight.y; g->toLight[2] = L->toLight.z;
    }
    u->counts[0] = (int32_t)n;
    memcpy(g_s3dVk.frameUbo.map, u, sizeof *u);
    return maps;
}

static void s3d_gpu_objectUniforms(const S3dBatch* b, S3dGpuObjectU* o) {
    const S3dSurface* s = &b->s;
    memset(o, 0, sizeof *o);
    s3d_gpu_copyMat(o->model, &b->model);
    s3d_gpu_copyMat(o->normalM, &b->normalM);
    o->color[0] = s->color.x; o->color[1] = s->color.y; o->color[2] = s->color.z; o->color[3] = s->opacity;
    o->emissive[0] = s->emissive.x; o->emissive[1] = s->emissive.y; o->emissive[2] = s->emissive.z; o->emissive[3] = s->cutoff;
    o->tm0[0] = s->tm[0]; o->tm0[1] = s->tm[1]; o->tm0[2] = s->tm[2]; o->tm0[3] = s->specular;
    o->tm1[0] = s->tm[3]; o->tm1[1] = s->tm[4]; o->tm1[2] = s->tm[5]; o->tm1[3] = s->shininess;
    o->flags[0] = s->smooth; o->flags[1] = s->twoSided; o->flags[2] = s->lit; o->flags[3] = s->receiveShadows;
    o->flags2[0] = s->layers; o->flags2[1] = b->tex != NULL; o->flags2[2] = b->bumpWanted; o->flags2[3] = b->blended;
    o->bump[0] = s->bump;
    if (b->tex) { o->bump[1] = 1.0f / (float)b->tex->w; o->bump[2] = 1.0f / (float)b->tex->h; }
}

static int s3d_gpu_flush(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, size_t lightCount, const S3dFlushSettings* st) {
    S3dGpuFrame* target;
    size_t i, nb = f->batchN, k;
    S3dVec3 lo = s3d_v3(1e30f, 1e30f, 1e30f), hi = s3d_v3(-1e30f, -1e30f, -1e30f);
    int anyCaster = 0, maps = 0, layerRes = 64, layers = 1, m, nblended = 0;
    VkRenderPassBeginInfo rp;
    VkClearValue clear[2];
    VkBufferImageCopy region;
    VkMemoryBarrier mb;
    if (!s3d_gpu_ready()) return 0;
    if ((uint32_t)f->w > g_s3dVk.maxImageDim || (uint32_t)f->h > g_s3dVk.maxImageDim) return 0;
    target = s3d_gpu_frameFor(f);
    if (target == NULL) return 0;
    /* the meshes and textures on the GPU; the bounds of the casters */
    for (i = 0; i < nb; i++) {
        S3dBatch* b = &f->batches[i];
        if (!s3d_gpu_meshFor((S3dMesh*)b->mesh)) return 0;
        if (b->tex && !s3d_gpu_texFor((S3dTexture*)b->tex)) return 0;
        if (st->shadows && b->s.castShadows && !b->blended && b->mesh->posN >= 3) { s3d_gpu_bounds(b, &lo, &hi); anyCaster = 1; }
    }
    /* the shadow maps (planned as the software renderer plans them) and the lights */
    f->ctx.shadowN = 0;
    if (st->shadows && anyCaster) s3d_planShadows(&f->ctx, lights, lightCount, st, lo, hi);
    s3d_prepareLights(&f->ctx, cam, lights, lightCount, st);
    for (k = 0; k < (size_t)f->ctx.shadowN; k++) for (m = 0; m < f->ctx.shadows[k].count; m++) {
        layerRes = s3d_maxi(layerRes, f->ctx.shadows[k].maps[m].res);
        if (++maps > 1) layers = maps;
    }
    if (layers > S3D_GPU_MAX_MAPS) layers = S3D_GPU_MAX_MAPS;
    if ((uint32_t)layers > g_s3dVk.maxLayers) layers = (int)g_s3dVk.maxLayers;
    if ((uint32_t)layerRes > g_s3dVk.maxImageDim) layerRes = (int)g_s3dVk.maxImageDim;
    while ((double)layerRes * layerRes * 4.0 * layers > 256.0 * 1024 * 1024 && layerRes > 64) layerRes /= 2;   /* at most 256 MB of shadow maps */
    if (!s3d_gpu_shadows(layerRes, layers)) return 0;
    maps = s3d_gpu_frameUniforms(f, cam, st, layerRes, lo, hi);
    /* the uniforms of the objects */
    if (!s3d_gpu_hostBuffer(&g_s3dVk.objectUbo, g_s3dVk.objectStride * (nb + 1), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT)) return 0;
    s3d_gpu_writeFrameSet();
    for (i = 0; i < nb; i++) s3d_gpu_objectUniforms(&f->batches[i], (S3dGpuObjectU*)(void*)((uint8_t*)g_s3dVk.objectUbo.map + i * g_s3dVk.objectStride));
    /* the transparent objects from far to near (stable) */
    if (!S3D_GROW(g_s3dVk.order, g_s3dVk.orderCap, nb + 1)) return 0;
    for (i = 0; i < nb; i++) if (f->batches[i].blended) {
        size_t j = (size_t)nblended++;
        while (j > 0 && f->batches[i].sortKey > f->batches[g_s3dVk.order[j - 1]].sortKey) { g_s3dVk.order[j] = g_s3dVk.order[j - 1]; j--; }
        g_s3dVk.order[j] = (int)i;
    }
    if (!s3d_gpu_begin()) return 0;
    /* the shadow maps */
    if (maps > 0 && s3d_gpu_pipeline(1, 0, 0, 1)) {
        for (m = 0; m < maps; m++) {
            int res = (int)g_s3dVk.frameData.maps[m].q[1];
            uint32_t dynamicZero = 0;
            memset(&rp, 0, sizeof rp);
            rp.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
            rp.renderPass = g_s3dVk.passShadow;
            rp.framebuffer = g_s3dVk.layerFbs[m];
            rp.renderArea.extent.width = (uint32_t)layerRes; rp.renderArea.extent.height = (uint32_t)layerRes;
            memset(clear, 0, sizeof clear);
            clear[0].color.float32[0] = 1e30f;
            clear[1].depthStencil.depth = 1.0f;
            rp.clearValueCount = 2; rp.pClearValues = clear;
            g_s3dVk.vkCmdBeginRenderPass(g_s3dVk.cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);
            s3d_gpu_viewport((uint32_t)res, (uint32_t)res);
            g_s3dVk.vkCmdBindPipeline(g_s3dVk.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_s3dVk.shadowPipe);
            g_s3dVk.vkCmdBindDescriptorSets(g_s3dVk.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_s3dVk.layoutShadow, 0, 1, &g_s3dVk.frameSet, 1, &dynamicZero);
            for (i = 0; i < nb; i++) {
                const S3dBatch* b = &f->batches[i];
                const S3dGpuMesh* gm = (const S3dGpuMesh*)b->mesh->gpu;
                S3dGpuShadowPush push;
                VkDeviceSize zero = 0;
                if (!b->s.castShadows || b->blended) continue;   /* transparent objects cast no shadow */
                s3d_gpu_copyMat(push.model, &b->model);
                push.map = m;
                g_s3dVk.vkCmdPushConstants(g_s3dVk.cmd, g_s3dVk.layoutShadow, VK_SHADER_STAGE_VERTEX_BIT, 0, (uint32_t)sizeof push, &push);
                g_s3dVk.vkCmdBindVertexBuffers(g_s3dVk.cmd, 0, 1, &gm->vb.buf, &zero);
                g_s3dVk.vkCmdBindIndexBuffer(g_s3dVk.cmd, gm->ib.buf, 0, VK_INDEX_TYPE_UINT32);
                g_s3dVk.vkCmdDrawIndexed(g_s3dVk.cmd, gm->indexCount, 1, 0, 0, 0);
            }
            g_s3dVk.vkCmdEndRenderPass(g_s3dVk.cmd);
        }
    }
    /* the picture: cleared with the background, the opaque objects in their order, then the transparent ones */
    memset(&rp, 0, sizeof rp);
    rp.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
    rp.renderPass = g_s3dVk.passMain;
    rp.framebuffer = target->fb;
    rp.renderArea.extent.width = (uint32_t)f->w; rp.renderArea.extent.height = (uint32_t)f->h;
    memset(clear, 0, sizeof clear);
    clear[0].color.float32[0] = (float)(f->clearColor & 255) / 255.0f;
    clear[0].color.float32[1] = (float)((f->clearColor >> 8) & 255) / 255.0f;
    clear[0].color.float32[2] = (float)((f->clearColor >> 16) & 255) / 255.0f;
    clear[0].color.float32[3] = (float)((f->clearColor >> 24) & 255) / 255.0f;
    clear[1].depthStencil.depth = 1.0f;
    rp.clearValueCount = 2; rp.pClearValues = clear;
    g_s3dVk.vkCmdBeginRenderPass(g_s3dVk.cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);
    s3d_gpu_viewport((uint32_t)f->w, (uint32_t)f->h);
    for (i = 0; i < nb; i++) {
        const S3dBatch* b = &f->batches[i];
        if (b->blended) continue;
        s3d_gpu_draw(b, i, b->s.twoSided ? 0 : 1, 0, 1);
    }
    for (k = 0; k < (size_t)nblended; k++) {
        int bi = g_s3dVk.order[k];
        const S3dBatch* b = &f->batches[bi];
        if (b->s.twoSided) { s3d_gpu_draw(b, (size_t)bi, 2, 1, 0); s3d_gpu_draw(b, (size_t)bi, 1, 1, 0); }   /* the far side first */
        else s3d_gpu_draw(b, (size_t)bi, 1, 1, 0);
    }
    g_s3dVk.vkCmdEndRenderPass(g_s3dVk.cmd);
    /* back to the CPU */
    memset(&region, 0, sizeof region);
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageExtent.width = (uint32_t)f->w; region.imageExtent.height = (uint32_t)f->h; region.imageExtent.depth = 1;
    g_s3dVk.vkCmdCopyImageToBuffer(g_s3dVk.cmd, target->color.img, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, target->readback.buf, 1, &region);
    memset(&mb, 0, sizeof mb);
    mb.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    mb.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    mb.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
    g_s3dVk.vkCmdPipelineBarrier(g_s3dVk.cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &mb, 0, NULL, 0, NULL);
    if (!s3d_gpu_submitAndWait()) { g_s3dVk.state = -1; return 0; }   /* (a lost device: the software renderer from now on) */
    memcpy(rgba, target->readback.map, (size_t)f->w * (size_t)f->h * 4);
    return 1;
}

/* ---- letting go ---- */
static void s3d_gpu_releaseMesh(S3dMesh* mesh) {
    S3dGpuMesh* g = (S3dGpuMesh*)mesh->gpu;
    if (g == NULL) return;
    s3d_gpu_freeBuffer(&g->vb);
    s3d_gpu_freeBuffer(&g->ib);
    free(g);
    mesh->gpu = NULL;
}
static void s3d_gpu_releaseTexture(S3dTexture* tex) {
    S3dGpuTex* g = (S3dGpuTex*)tex->gpu;
    if (g == NULL) return;
    s3d_gpu_freeTex(g);
    free(g);
    tex->gpu = NULL;
}
static void s3d_gpu_releaseFrame(S3dFrame* f) {
    S3dGpuFrame* g = (S3dGpuFrame*)f->gpu;
    if (g == NULL) return;
    s3d_gpu_freeFrameTargets(g);
    free(g);
    f->gpu = NULL;
}
/* The end of a program (the VM keeps the library): the shadow maps go back to their smallest size; the device stays for the next program. */
static void s3d_gpu_shutdown(void) {
    if (g_s3dVk.state == 1 && g_s3dVk.shadowRes != 64) s3d_gpu_shadows(64, 1);
}
static const char* s3d_gpu_deviceName(void) { return s3d_gpu_ready() ? g_s3dVk.deviceName : ""; }
static const char* s3d_gpu_error(void) { return g_s3dVk.state == -1 ? g_s3dVk.error : ""; }

#endif
#endif /* FIRE_S3D_GPU_VK_H */
