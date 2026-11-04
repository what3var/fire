/* fire graphics: the GPU backend (docs/UI_PLATFORM.md, part 4) - a framebuffer may also live on the GPU (Vulkan, loaded at run time). Drawing that the GPU does
 * goes into a list of commands; it runs when the pixels are needed: a read of the framebuffer (GetPixel, a window that shows it, a drawing the GPU does not do)
 * fetches them back - only when the GPU drew since (a dirty flag) - and a drawing of the CPU after that uploads them again before the GPU draws on.
 * The software is the reference: the GPU draws the same shapes (the same coverage at the edges), small differences in the rounding of colours are allowed.
 *
 *   fire_gfx_gpuAttach(fb)          the framebuffer gets its twin on the GPU (an RGBA one; 0: there is no GPU, or not for this one) - Framebuffer.Accelerated
 *   fire_gfx_gpuDetach(fb)          lets go of it (the pixels are fetched back first)
 *   fire_gfx_gpuShape(...)          a rounded rectangle (filled or outlined; radius 0: a plain one) on the GPU - 0: it cannot, the caller draws it in software
 *   fire_gfx_gpuError()             why there is no GPU
 *
 * FIRE_GFX_GPU=1 in the environment accelerates every RGBA framebuffer (tests, trying it out); FIRE_GFX_GPU=cpu also takes a Vulkan device that is a CPU
 * (Mesa's lavapipe). Without Vulkan, on a board and in the browser everything stays software. */
#ifndef FIRE_GFX_GPU_H
#define FIRE_GFX_GPU_H

#include "fire_gfx.h"
#include "fire_gfx_text.h"

#if defined(ESP_PLATFORM) || defined(__EMSCRIPTEN__) || defined(FIRE_GFX_NO_GPU) || defined(FIRE_PLATFORM_FREERTOS_H)

static int fire_gfx_gpuAttach(FireGfxFramebuffer* fb) { (void)fb; return 0; }
static void fire_gfx_gpuDetach(FireGfxFramebuffer* fb) { (void)fb; }
static int fire_gfx_gpuShape(FireGfxFramebuffer* fb, int cl, int ct, int cr, int cb, int x, int y, int w, int h, int radius, int stroke, uint32_t rgba, int copy) {
    (void)fb; (void)cl; (void)ct; (void)cr; (void)cb; (void)x; (void)y; (void)w; (void)h; (void)radius; (void)stroke; (void)rgba; (void)copy;
    return 0;
}
static const char* fire_gfx_gpuError(void) { return "There is no GPU backend in this build."; }
static int fire_gfx_gpuWanted(void) { return 0; }
static int fire_gfx_gpuText(FireGfxFramebuffer* fb, const int* clip, int copy, FireGfxTextFont* font, int small, int cellW, int cellH, int size, int x, int y,
                            const uint16_t* text, uint32_t n, uint32_t fg, int hasBg, uint32_t bg) {
    (void)fb; (void)clip; (void)copy; (void)font; (void)small; (void)cellW; (void)cellH; (void)size; (void)x; (void)y; (void)text; (void)n; (void)fg; (void)hasBg; (void)bg;
    return 0;
}
static int fire_gfx_gpuBlit(FireGfxFramebuffer* dst, const FireGfxFramebuffer* src, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode, int blend,
                            int cl, int ct, int cr, int cb) {
    (void)dst; (void)src; (void)sx; (void)sy; (void)sw; (void)sh; (void)dx; (void)dy; (void)dw; (void)dh; (void)mode; (void)blend; (void)cl; (void)ct; (void)cr; (void)cb;
    return 0;
}

#else

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#else
#  include <dlfcn.h>
#endif
#ifndef FIRE_VK_DECLS_H
#include "fire_gfx_vk.h"   /* (the same file as scene3d/native/s3d_vk.h: a native build with both has them once) */
#endif
#include "fire_gfx_shaders.h"

#define FIRE_GFX_NO_SET ((VkDescriptorSet)0)   /* (no descriptor set: VK_NULL_HANDLE) */

#define FIRE_GFX_VK_INSTANCE_FUNCTIONS(X) \
    X(vkDestroyInstance) X(vkEnumeratePhysicalDevices) X(vkGetPhysicalDeviceProperties) X(vkGetPhysicalDeviceQueueFamilyProperties) \
    X(vkGetPhysicalDeviceMemoryProperties) X(vkGetPhysicalDeviceFormatProperties) X(vkCreateDevice) X(vkGetDeviceProcAddr)
#define FIRE_GFX_VK_DEVICE_FUNCTIONS(X) \
    X(vkDestroyDevice) X(vkGetDeviceQueue) X(vkDeviceWaitIdle) X(vkQueueSubmit) X(vkCreateCommandPool) X(vkDestroyCommandPool) \
    X(vkAllocateCommandBuffers) X(vkBeginCommandBuffer) X(vkEndCommandBuffer) X(vkResetCommandBuffer) X(vkCreateFence) X(vkDestroyFence) \
    X(vkWaitForFences) X(vkResetFences) X(vkCreateImage) X(vkDestroyImage) X(vkGetImageMemoryRequirements) X(vkBindImageMemory) \
    X(vkCreateImageView) X(vkDestroyImageView) X(vkCreateBuffer) X(vkDestroyBuffer) X(vkGetBufferMemoryRequirements) X(vkBindBufferMemory) \
    X(vkAllocateMemory) X(vkFreeMemory) X(vkMapMemory) X(vkUnmapMemory) X(vkCreateRenderPass) X(vkDestroyRenderPass) X(vkCreateFramebuffer) \
    X(vkDestroyFramebuffer) X(vkCreateShaderModule) X(vkDestroyShaderModule) X(vkCreatePipelineLayout) X(vkDestroyPipelineLayout) \
    X(vkCreateGraphicsPipelines) X(vkDestroyPipeline) X(vkCmdBeginRenderPass) X(vkCmdEndRenderPass) X(vkCmdBindPipeline) X(vkCmdBindVertexBuffers) \
    X(vkCmdSetViewport) X(vkCmdSetScissor) X(vkCmdPushConstants) X(vkCmdDraw) X(vkCmdCopyBufferToImage) X(vkCmdCopyImageToBuffer) X(vkCmdPipelineBarrier) \
    X(vkCreateDescriptorSetLayout) X(vkDestroyDescriptorSetLayout) X(vkCreateDescriptorPool) X(vkDestroyDescriptorPool) X(vkAllocateDescriptorSets) \
    X(vkFreeDescriptorSets) X(vkUpdateDescriptorSets) X(vkCreateSampler) X(vkDestroySampler) X(vkCmdBindDescriptorSets)

/* One shape for the GPU (the vertex input of shaders/gfx_shape.vert): 52 bytes. */
typedef struct FireGfxGpuShape { float quad[4], box[4], param[4]; uint32_t color; } FireGfxGpuShape;
/* One picture for the GPU (the vertex input of shaders/gfx_image.vert): a glyph of the atlas or a blit - 68 bytes. */
typedef struct FireGfxGpuImage { float quad[4]; int32_t dst[4], src[4], mode[4]; uint32_t color; } FireGfxGpuImage;
enum { FIRE_GFX_GPU_GLYPH = 0, FIRE_GFX_GPU_COPY = 1, FIRE_GFX_GPU_TRANSPARENT = 2, FIRE_GFX_GPU_BLEND = 3 };
/* A run of shapes (kind 0) or pictures (kind 1, from the texture of `set`) with the same clip and pipeline - in the order they were drawn. */
typedef struct FireGfxGpuBatch { int kind; int clip[4]; int copy; VkDescriptorSet set; uint32_t first, count; } FireGfxGpuBatch;

typedef struct FireGfxVkBuffer { VkBuffer buffer; VkDeviceMemory memory; void* mapped; VkDeviceSize size; } FireGfxVkBuffer;

/* The twin of a framebuffer on the GPU. */
typedef struct FireGfxGpuTwin {
    int width, height;
    VkImage image; VkDeviceMemory memory; VkImageView view; VkFramebuffer framebuffer;
    FireGfxVkBuffer staging;          /* host memory of the size of the picture: uploads and readbacks */
    int cpuValid, gpuValid;           /* who holds the current pixels (both: the same) */
    FireGfxGpuShape* shapes; uint32_t shapeCount, shapeCap;
    FireGfxGpuImage* images; uint32_t imageCount, imageCap;
    FireGfxGpuBatch* batches; uint32_t batchCount, batchCap;
    VkDescriptorSet set;              /* the picture as a texture (a blit from this framebuffer into another) */
    FireGfxFramebuffer* fb;           /* whose twin it is */
    struct FireGfxGpuTwin* nextPending; int listed;   /* in the list of the twins with drawing to do (in the order of their first command) */
} FireGfxGpuTwin;

/* The glyphs on the GPU: one texture (one byte of coverage per pixel), filled in shelves; a copy in host memory (`mirror`) goes up where it changed. */
#define FIRE_GFX_ATLAS_SIZE 2048
#define FIRE_GFX_ATLAS_SLOTS 8192
typedef struct FireGfxAtlasSlot { uint64_t key; int x, y, w, h; } FireGfxAtlasSlot;

static struct {
    int state;                 /* 0: not tried yet, 1: ready, -1: there is none (error says why) */
    char error[256];
    char deviceName[256];
    void* lib;
    PFN_vkGetInstanceProcAddr vkGetInstanceProcAddr;
    PFN_vkCreateInstance vkCreateInstance;
#define FIRE_GFX_VK_DECLARE(name) PFN_##name name;
    FIRE_GFX_VK_INSTANCE_FUNCTIONS(FIRE_GFX_VK_DECLARE)
    FIRE_GFX_VK_DEVICE_FUNCTIONS(FIRE_GFX_VK_DECLARE)
#undef FIRE_GFX_VK_DECLARE
    VkInstance instance;
    VkPhysicalDevice phys;
    VkDevice device;
    VkQueue queue;
    uint32_t family;
    VkPhysicalDeviceMemoryProperties memProps;
    VkCommandPool pool;
    VkCommandBuffer cmd;
    VkFence fence;
    VkRenderPass pass;
    VkShaderModule vert, frag;
    VkPipelineLayout layout;
    VkPipeline blend, copy;
    FireGfxVkBuffer shapes;    /* the shapes of a flush (host visible) */
    /* pictures: glyphs and blits */
    VkShaderModule imageVert, imageFrag;
    VkDescriptorSetLayout setLayout;
    VkPipelineLayout imageLayout;
    VkPipeline imageBlend, imageCopy;
    VkDescriptorPool descPool;
    VkSampler sampler;
    FireGfxVkBuffer images;    /* the pictures of a flush */
    /* the atlas of the glyphs */
    VkImage atlas; VkDeviceMemory atlasMemory; VkImageView atlasView; VkDescriptorSet atlasSet;
    FireGfxVkBuffer atlasMirror;
    int atlasReady;            /* its layout is GENERAL (set by its first upload) */
    int shelfX, shelfY, shelfH;
    int dirty[4];              /* x0, y0, x1, y1 of what changed in the mirror (x1 <= x0: nothing) */
    FireGfxAtlasSlot* slots; int slotCount;
    FireGfxGpuTwin* pending;   /* the twins with drawing to do, the oldest first */
} g_fireGfxVk;

static const char* fire_gfx_gpuError(void) { return g_fireGfxVk.state > 0 ? "" : g_fireGfxVk.error[0] ? g_fireGfxVk.error : "There is no GPU (not tried yet: set Accelerated first)."; }
static void fire_gfx_gpuFail(const char* text) { snprintf(g_fireGfxVk.error, sizeof g_fireGfxVk.error, "%s", text); }
static int fire_gfx_gpuCheck(VkResult r, const char* what) {
    if (r == VK_SUCCESS) return 1;
    snprintf(g_fireGfxVk.error, sizeof g_fireGfxVk.error, "%s failed (VkResult %d).", what, (int)r);
    return 0;
}

/* FIRE_GFX_GPU=1 (or cpu): every RGBA framebuffer is accelerated. */
static int fire_gfx_gpuWanted(void) {
    const char* v = getenv("FIRE_GFX_GPU");
    return v != NULL && (strcmp(v, "1") == 0 || strcmp(v, "cpu") == 0);
}

/* ---- Vulkan ---- */

static int fire_gfx_gpuLoad(void) {
#ifdef _WIN32
    HMODULE lib = LoadLibraryA("vulkan-1.dll");
    if (lib == NULL) { fire_gfx_gpuFail("There is no Vulkan on this machine (vulkan-1.dll was not found)."); return 0; }
    g_fireGfxVk.lib = (void*)lib;
    g_fireGfxVk.vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)(void (*)(void))GetProcAddress(lib, "vkGetInstanceProcAddr");
#else
#ifdef __APPLE__
    static const char* names[] = {"libvulkan.1.dylib", "libvulkan.dylib", "libMoltenVK.dylib", "/usr/local/lib/libvulkan.1.dylib", "/opt/homebrew/lib/libvulkan.1.dylib", NULL};
#else
    static const char* names[] = {"libvulkan.so.1", "libvulkan.so", NULL};
#endif
    int i;
    void* sym;
    for (i = 0; names[i] != NULL && g_fireGfxVk.lib == NULL; i++) g_fireGfxVk.lib = dlopen(names[i], RTLD_NOW | RTLD_LOCAL);
    if (g_fireGfxVk.lib == NULL) { fire_gfx_gpuFail("There is no Vulkan on this machine (the Vulkan loader was not found)."); return 0; }
    sym = dlsym(g_fireGfxVk.lib, "vkGetInstanceProcAddr");
    memcpy(&g_fireGfxVk.vkGetInstanceProcAddr, &sym, sizeof sym);
#endif
    if (g_fireGfxVk.vkGetInstanceProcAddr == NULL) { fire_gfx_gpuFail("The Vulkan loader has no vkGetInstanceProcAddr."); return 0; }
    g_fireGfxVk.vkCreateInstance = (PFN_vkCreateInstance)g_fireGfxVk.vkGetInstanceProcAddr(NULL, "vkCreateInstance");
    if (g_fireGfxVk.vkCreateInstance == NULL) { fire_gfx_gpuFail("The Vulkan loader is incomplete."); return 0; }
    return 1;
}

static int fire_gfx_gpuInstance(void) {
    VkApplicationInfo app;
    VkInstanceCreateInfo ci;
    VkResult r;
    memset(&app, 0, sizeof app);
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName = "fire graphics";
    app.pEngineName = "fire graphics";
    app.apiVersion = (uint32_t)1 << 22;   /* Vulkan 1.0 */
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ci.pApplicationInfo = &app;
    r = g_fireGfxVk.vkCreateInstance(&ci, NULL, &g_fireGfxVk.instance);
    if (r != VK_SUCCESS) {
        if (r == VK_ERROR_INCOMPATIBLE_DRIVER) fire_gfx_gpuFail("There is no Vulkan driver on this machine.");
        else fire_gfx_gpuCheck(r, "vkCreateInstance");
        g_fireGfxVk.instance = NULL;
        return 0;
    }
#define FIRE_GFX_VK_LOAD_I(name) \
    g_fireGfxVk.name = (PFN_##name)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, #name); \
    if (g_fireGfxVk.name == NULL) { fire_gfx_gpuFail("The Vulkan loader lacks " #name "."); return 0; }
    FIRE_GFX_VK_INSTANCE_FUNCTIONS(FIRE_GFX_VK_LOAD_I)
#undef FIRE_GFX_VK_LOAD_I
    return 1;
}

/* A GPU (discrete before integrated before virtual) with a queue that draws; a CPU device only when asked for (FIRE_GFX_GPU=cpu). */
static int fire_gfx_gpuPick(void) {
    uint32_t count = 0, i, best = 0, bestFamily = 0;
    int bestScore = 0;
    VkPhysicalDevice* list;
    const char* env = getenv("FIRE_GFX_GPU");
    int allowCpu = env != NULL && strcmp(env, "cpu") == 0;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkEnumeratePhysicalDevices(g_fireGfxVk.instance, &count, NULL), "vkEnumeratePhysicalDevices")) return 0;
    if (count == 0) { fire_gfx_gpuFail("Vulkan finds no GPU on this machine."); return 0; }
    list = (VkPhysicalDevice*)malloc(sizeof(VkPhysicalDevice) * count);
    if (list == NULL) { fire_gfx_gpuFail("Out of memory."); return 0; }
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkEnumeratePhysicalDevices(g_fireGfxVk.instance, &count, list), "vkEnumeratePhysicalDevices")) { free(list); return 0; }
    for (i = 0; i < count; i++) {
        VkPhysicalDeviceProperties props;
        VkQueueFamilyProperties* fams;
        uint32_t famCount = 0, k;
        int score;
        g_fireGfxVk.vkGetPhysicalDeviceProperties(list[i], &props);
        switch (props.deviceType) {
            case VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU: score = 5; break;
            case VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU: score = 4; break;
            case VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU: score = 3; break;
            case VK_PHYSICAL_DEVICE_TYPE_CPU: score = allowCpu ? 1 : 0; break;
            default: score = 2; break;
        }
        if (score <= bestScore) continue;
        g_fireGfxVk.vkGetPhysicalDeviceQueueFamilyProperties(list[i], &famCount, NULL);
        fams = (VkQueueFamilyProperties*)malloc(sizeof(VkQueueFamilyProperties) * (famCount + 1));
        if (fams == NULL) continue;
        g_fireGfxVk.vkGetPhysicalDeviceQueueFamilyProperties(list[i], &famCount, fams);
        for (k = 0; k < famCount; k++)
            if ((fams[k].queueFlags & VK_QUEUE_GRAPHICS_BIT) && fams[k].queueCount > 0) { bestScore = score; best = i; bestFamily = k; break; }
        free(fams);
    }
    if (bestScore == 0) {
        free(list);
        fire_gfx_gpuFail(allowCpu ? "Vulkan finds no device that can draw." : "Vulkan finds no GPU on this machine (only a CPU device: FIRE_GFX_GPU=cpu takes it).");
        return 0;
    }
    g_fireGfxVk.phys = list[best];
    g_fireGfxVk.family = bestFamily;
    free(list);
    {
        VkPhysicalDeviceProperties props;
        g_fireGfxVk.vkGetPhysicalDeviceProperties(g_fireGfxVk.phys, &props);
        snprintf(g_fireGfxVk.deviceName, sizeof g_fireGfxVk.deviceName, "%s", props.deviceName);
    }
    g_fireGfxVk.vkGetPhysicalDeviceMemoryProperties(g_fireGfxVk.phys, &g_fireGfxVk.memProps);
    return 1;
}

static int fire_gfx_gpuDevice(void) {
    float priority = 1.0f;
    VkDeviceQueueCreateInfo qi;
    VkDeviceCreateInfo di;
    VkCommandPoolCreateInfo pi;
    VkCommandBufferAllocateInfo ai;
    VkFenceCreateInfo fi;
    memset(&qi, 0, sizeof qi);
    qi.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qi.queueFamilyIndex = g_fireGfxVk.family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &priority;
    memset(&di, 0, sizeof di);
    di.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    di.queueCreateInfoCount = 1;
    di.pQueueCreateInfos = &qi;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateDevice(g_fireGfxVk.phys, &di, NULL, &g_fireGfxVk.device), "vkCreateDevice")) return 0;
#define FIRE_GFX_VK_LOAD_D(name) \
    g_fireGfxVk.name = (PFN_##name)g_fireGfxVk.vkGetDeviceProcAddr(g_fireGfxVk.device, #name); \
    if (g_fireGfxVk.name == NULL) { fire_gfx_gpuFail("The Vulkan driver lacks " #name "."); return 0; }
    FIRE_GFX_VK_DEVICE_FUNCTIONS(FIRE_GFX_VK_LOAD_D)
#undef FIRE_GFX_VK_LOAD_D
    g_fireGfxVk.vkGetDeviceQueue(g_fireGfxVk.device, g_fireGfxVk.family, 0, &g_fireGfxVk.queue);
    memset(&pi, 0, sizeof pi);
    pi.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pi.queueFamilyIndex = g_fireGfxVk.family;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateCommandPool(g_fireGfxVk.device, &pi, NULL, &g_fireGfxVk.pool), "vkCreateCommandPool")) return 0;
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    ai.commandPool = g_fireGfxVk.pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = 1;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkAllocateCommandBuffers(g_fireGfxVk.device, &ai, &g_fireGfxVk.cmd), "vkAllocateCommandBuffers")) return 0;
    memset(&fi, 0, sizeof fi);
    fi.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    return fire_gfx_gpuCheck(g_fireGfxVk.vkCreateFence(g_fireGfxVk.device, &fi, NULL, &g_fireGfxVk.fence), "vkCreateFence");
}

static int fire_gfx_gpuMemoryType(uint32_t bits, VkMemoryPropertyFlags want) {
    uint32_t i;
    for (i = 0; i < g_fireGfxVk.memProps.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (g_fireGfxVk.memProps.memoryTypes[i].propertyFlags & want) == want) return (int)i;
    return -1;
}

/* A buffer in host memory that the GPU reads and writes (mapped). */
static int fire_gfx_gpuBuffer(FireGfxVkBuffer* b, VkDeviceSize size, VkBufferUsageFlags usage) {
    VkBufferCreateInfo bi;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    int type;
    memset(b, 0, sizeof *b);
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bi.size = size;
    bi.usage = usage;
    bi.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateBuffer(g_fireGfxVk.device, &bi, NULL, &b->buffer), "vkCreateBuffer")) return 0;
    g_fireGfxVk.vkGetBufferMemoryRequirements(g_fireGfxVk.device, b->buffer, &req);
    type = fire_gfx_gpuMemoryType(req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (type < 0) { fire_gfx_gpuFail("The GPU has no memory the CPU can see."); return 0; }
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = (uint32_t)type;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkAllocateMemory(g_fireGfxVk.device, &ai, NULL, &b->memory), "vkAllocateMemory")) return 0;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkBindBufferMemory(g_fireGfxVk.device, b->buffer, b->memory, 0), "vkBindBufferMemory")) return 0;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkMapMemory(g_fireGfxVk.device, b->memory, 0, size, 0, &b->mapped), "vkMapMemory")) return 0;
    b->size = size;
    return 1;
}
static void fire_gfx_gpuFreeBuffer(FireGfxVkBuffer* b) {
    if (b->mapped) g_fireGfxVk.vkUnmapMemory(g_fireGfxVk.device, b->memory);
    if (b->buffer) g_fireGfxVk.vkDestroyBuffer(g_fireGfxVk.device, b->buffer, NULL);
    if (b->memory) g_fireGfxVk.vkFreeMemory(g_fireGfxVk.device, b->memory, NULL);
    memset(b, 0, sizeof *b);
}

static int fire_gfx_gpuModule(const uint32_t* code, size_t size, VkShaderModule* out) {
    VkShaderModuleCreateInfo ci;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    ci.codeSize = size;
    ci.pCode = code;
    return fire_gfx_gpuCheck(g_fireGfxVk.vkCreateShaderModule(g_fireGfxVk.device, &ci, NULL, out), "vkCreateShaderModule");
}

/* The render pass (the picture stays: load and store, in the GENERAL layout throughout) and the two pipelines of the shapes: blending and copying. */
static int fire_gfx_gpuPipelines(void) {
    VkAttachmentDescription att;
    VkAttachmentReference ref;
    VkSubpassDescription sub;
    VkRenderPassCreateInfo rp;
    VkPushConstantRange push;
    VkPipelineLayoutCreateInfo li;
    VkPipelineShaderStageCreateInfo stages[2];
    VkVertexInputBindingDescription bind;
    VkVertexInputAttributeDescription attrs[4];
    VkPipelineVertexInputStateCreateInfo vi;
    VkPipelineInputAssemblyStateCreateInfo ia;
    VkPipelineViewportStateCreateInfo vp;
    VkPipelineRasterizationStateCreateInfo rs;
    VkPipelineMultisampleStateCreateInfo ms;
    VkPipelineColorBlendAttachmentState blendAtt;
    VkPipelineColorBlendStateCreateInfo cb;
    VkDynamicState dyn[2] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
    VkPipelineDynamicStateCreateInfo ds;
    VkGraphicsPipelineCreateInfo gi;
    int k;
    memset(&att, 0, sizeof att);
    att.format = VK_FORMAT_R8G8B8A8_UNORM;
    att.samples = VK_SAMPLE_COUNT_1_BIT;
    att.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
    att.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    att.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    att.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att.initialLayout = VK_IMAGE_LAYOUT_GENERAL;
    att.finalLayout = VK_IMAGE_LAYOUT_GENERAL;
    ref.attachment = 0;
    ref.layout = VK_IMAGE_LAYOUT_GENERAL;
    memset(&sub, 0, sizeof sub);
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ref;
    memset(&rp, 0, sizeof rp);
    rp.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
    rp.attachmentCount = 1;
    rp.pAttachments = &att;
    rp.subpassCount = 1;
    rp.pSubpasses = &sub;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateRenderPass(g_fireGfxVk.device, &rp, NULL, &g_fireGfxVk.pass), "vkCreateRenderPass")) return 0;
    if (!fire_gfx_gpuModule(fire_gfx_spv_shape_vert, sizeof fire_gfx_spv_shape_vert, &g_fireGfxVk.vert)) return 0;
    if (!fire_gfx_gpuModule(fire_gfx_spv_shape_frag, sizeof fire_gfx_spv_shape_frag, &g_fireGfxVk.frag)) return 0;
    memset(&push, 0, sizeof push);
    push.stageFlags = VK_SHADER_STAGE_VERTEX_BIT;
    push.size = 2 * sizeof(float);
    memset(&li, 0, sizeof li);
    li.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    li.pushConstantRangeCount = 1;
    li.pPushConstantRanges = &push;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreatePipelineLayout(g_fireGfxVk.device, &li, NULL, &g_fireGfxVk.layout), "vkCreatePipelineLayout")) return 0;
    memset(stages, 0, sizeof stages);
    for (k = 0; k < 2; k++) { stages[k].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO; stages[k].pName = "main"; }
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT; stages[0].module = g_fireGfxVk.vert;
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].module = g_fireGfxVk.frag;
    bind.binding = 0;
    bind.stride = sizeof(FireGfxGpuShape);
    bind.inputRate = VK_VERTEX_INPUT_RATE_INSTANCE;
    for (k = 0; k < 4; k++) { attrs[k].location = (uint32_t)k; attrs[k].binding = 0; attrs[k].format = VK_FORMAT_R32G32B32A32_SFLOAT; attrs[k].offset = (uint32_t)(k * 16); }
    attrs[3].format = VK_FORMAT_R8G8B8A8_UNORM;
    memset(&vi, 0, sizeof vi);
    vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    vi.vertexBindingDescriptionCount = 1;
    vi.pVertexBindingDescriptions = &bind;
    vi.vertexAttributeDescriptionCount = 4;
    vi.pVertexAttributeDescriptions = attrs;
    memset(&ia, 0, sizeof ia);
    ia.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    memset(&vp, 0, sizeof vp);
    vp.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
    vp.viewportCount = 1;
    vp.scissorCount = 1;
    memset(&rs, 0, sizeof rs);
    rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = VK_CULL_MODE_NONE;
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rs.lineWidth = 1.0f;
    memset(&ms, 0, sizeof ms);
    ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    memset(&blendAtt, 0, sizeof blendAtt);
    blendAtt.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    memset(&cb, 0, sizeof cb);
    cb.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
    cb.attachmentCount = 1;
    cb.pAttachments = &blendAtt;
    memset(&ds, 0, sizeof ds);
    ds.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
    ds.dynamicStateCount = 2;
    ds.pDynamicStates = dyn;
    memset(&gi, 0, sizeof gi);
    gi.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    gi.stageCount = 2;
    gi.pStages = stages;
    gi.pVertexInputState = &vi;
    gi.pInputAssemblyState = &ia;
    gi.pViewportState = &vp;
    gi.pRasterizationState = &rs;
    gi.pMultisampleState = &ms;
    gi.pColorBlendState = &cb;
    gi.pDynamicState = &ds;
    gi.layout = g_fireGfxVk.layout;
    gi.renderPass = g_fireGfxVk.pass;
    /* copying: the colour as it is (with its alpha) */
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateGraphicsPipelines(g_fireGfxVk.device, NULL, 1, &gi, NULL, &g_fireGfxVk.copy), "vkCreateGraphicsPipelines")) return 0;
    /* blending as fire_gfx_mix: colour src*a + dst*(1-a), alpha a + dst*(1-a) */
    blendAtt.blendEnable = VK_TRUE;
    blendAtt.srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA;
    blendAtt.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blendAtt.colorBlendOp = VK_BLEND_OP_ADD;
    blendAtt.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    blendAtt.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blendAtt.alphaBlendOp = VK_BLEND_OP_ADD;
    return fire_gfx_gpuCheck(g_fireGfxVk.vkCreateGraphicsPipelines(g_fireGfxVk.device, NULL, 1, &gi, NULL, &g_fireGfxVk.blend), "vkCreateGraphicsPipelines");
}

/* A texture's descriptor set (a combined image sampler of `view`); FIRE_GFX_NO_SET when the pool is out. */
static VkDescriptorSet fire_gfx_gpuSetFor(VkImageView view) {
    VkDescriptorSetAllocateInfo ai;
    VkDescriptorSet set = FIRE_GFX_NO_SET;
    VkDescriptorImageInfo ii;
    VkWriteDescriptorSet w;
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    ai.descriptorPool = g_fireGfxVk.descPool;
    ai.descriptorSetCount = 1;
    ai.pSetLayouts = &g_fireGfxVk.setLayout;
    if (g_fireGfxVk.vkAllocateDescriptorSets(g_fireGfxVk.device, &ai, &set) != VK_SUCCESS) return FIRE_GFX_NO_SET;
    memset(&ii, 0, sizeof ii);
    ii.sampler = g_fireGfxVk.sampler;
    ii.imageView = view;
    ii.imageLayout = VK_IMAGE_LAYOUT_GENERAL;
    memset(&w, 0, sizeof w);
    w.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    w.dstSet = set;
    w.descriptorCount = 1;
    w.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    w.pImageInfo = &ii;
    g_fireGfxVk.vkUpdateDescriptorSets(g_fireGfxVk.device, 1, &w, 0, NULL);
    return set;
}

/* The pipelines of the pictures (shaders/gfx_image.*): a texture through a descriptor set, blending or copying; the sampler, the pool of the sets and the atlas. */
static int fire_gfx_gpuImagePipelines(void) {
    VkDescriptorSetLayoutBinding binding;
    VkDescriptorSetLayoutCreateInfo sl;
    VkDescriptorPoolSize poolSize;
    VkDescriptorPoolCreateInfo dp;
    VkSamplerCreateInfo si;
    VkPushConstantRange push;
    VkPipelineLayoutCreateInfo li;
    VkPipelineShaderStageCreateInfo stages[2];
    VkVertexInputBindingDescription bind;
    VkVertexInputAttributeDescription attrs[5];
    VkPipelineVertexInputStateCreateInfo vi;
    VkPipelineInputAssemblyStateCreateInfo ia;
    VkPipelineViewportStateCreateInfo vp;
    VkPipelineRasterizationStateCreateInfo rs;
    VkPipelineMultisampleStateCreateInfo ms;
    VkPipelineColorBlendAttachmentState blendAtt;
    VkPipelineColorBlendStateCreateInfo cb;
    VkDynamicState dyn[2] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
    VkPipelineDynamicStateCreateInfo ds;
    VkGraphicsPipelineCreateInfo gi;
    VkImageCreateInfo ii;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    VkImageViewCreateInfo vw;
    int k, type;
    memset(&binding, 0, sizeof binding);
    binding.binding = 0;
    binding.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    binding.descriptorCount = 1;
    binding.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    memset(&sl, 0, sizeof sl);
    sl.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    sl.bindingCount = 1;
    sl.pBindings = &binding;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateDescriptorSetLayout(g_fireGfxVk.device, &sl, NULL, &g_fireGfxVk.setLayout), "vkCreateDescriptorSetLayout")) return 0;
    poolSize.type = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    poolSize.descriptorCount = 4096;
    memset(&dp, 0, sizeof dp);
    dp.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    dp.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    dp.maxSets = 4096;
    dp.poolSizeCount = 1;
    dp.pPoolSizes = &poolSize;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateDescriptorPool(g_fireGfxVk.device, &dp, NULL, &g_fireGfxVk.descPool), "vkCreateDescriptorPool")) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    si.magFilter = VK_FILTER_NEAREST;
    si.minFilter = VK_FILTER_NEAREST;
    si.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    si.addressModeU = si.addressModeV = si.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateSampler(g_fireGfxVk.device, &si, NULL, &g_fireGfxVk.sampler), "vkCreateSampler")) return 0;
    if (!fire_gfx_gpuModule(fire_gfx_spv_image_vert, sizeof fire_gfx_spv_image_vert, &g_fireGfxVk.imageVert)) return 0;
    if (!fire_gfx_gpuModule(fire_gfx_spv_image_frag, sizeof fire_gfx_spv_image_frag, &g_fireGfxVk.imageFrag)) return 0;
    memset(&push, 0, sizeof push);
    push.stageFlags = VK_SHADER_STAGE_VERTEX_BIT;
    push.size = 2 * sizeof(float);
    memset(&li, 0, sizeof li);
    li.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    li.setLayoutCount = 1;
    li.pSetLayouts = &g_fireGfxVk.setLayout;
    li.pushConstantRangeCount = 1;
    li.pPushConstantRanges = &push;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreatePipelineLayout(g_fireGfxVk.device, &li, NULL, &g_fireGfxVk.imageLayout), "vkCreatePipelineLayout")) return 0;
    memset(stages, 0, sizeof stages);
    for (k = 0; k < 2; k++) { stages[k].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO; stages[k].pName = "main"; }
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT; stages[0].module = g_fireGfxVk.imageVert;
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].module = g_fireGfxVk.imageFrag;
    bind.binding = 0;
    bind.stride = sizeof(FireGfxGpuImage);
    bind.inputRate = VK_VERTEX_INPUT_RATE_INSTANCE;
    for (k = 0; k < 5; k++) { attrs[k].location = (uint32_t)k; attrs[k].binding = 0; attrs[k].format = VK_FORMAT_R32G32B32A32_SINT; attrs[k].offset = (uint32_t)(k * 16); }
    attrs[0].format = VK_FORMAT_R32G32B32A32_SFLOAT;
    attrs[4].format = VK_FORMAT_R8G8B8A8_UNORM;
    memset(&vi, 0, sizeof vi);
    vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    vi.vertexBindingDescriptionCount = 1;
    vi.pVertexBindingDescriptions = &bind;
    vi.vertexAttributeDescriptionCount = 5;
    vi.pVertexAttributeDescriptions = attrs;
    memset(&ia, 0, sizeof ia);
    ia.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    memset(&vp, 0, sizeof vp);
    vp.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
    vp.viewportCount = 1;
    vp.scissorCount = 1;
    memset(&rs, 0, sizeof rs);
    rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = VK_CULL_MODE_NONE;
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rs.lineWidth = 1.0f;
    memset(&ms, 0, sizeof ms);
    ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    memset(&blendAtt, 0, sizeof blendAtt);
    blendAtt.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    memset(&cb, 0, sizeof cb);
    cb.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
    cb.attachmentCount = 1;
    cb.pAttachments = &blendAtt;
    memset(&ds, 0, sizeof ds);
    ds.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO;
    ds.dynamicStateCount = 2;
    ds.pDynamicStates = dyn;
    memset(&gi, 0, sizeof gi);
    gi.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    gi.stageCount = 2;
    gi.pStages = stages;
    gi.pVertexInputState = &vi;
    gi.pInputAssemblyState = &ia;
    gi.pViewportState = &vp;
    gi.pRasterizationState = &rs;
    gi.pMultisampleState = &ms;
    gi.pColorBlendState = &cb;
    gi.pDynamicState = &ds;
    gi.layout = g_fireGfxVk.imageLayout;
    gi.renderPass = g_fireGfxVk.pass;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateGraphicsPipelines(g_fireGfxVk.device, NULL, 1, &gi, NULL, &g_fireGfxVk.imageCopy), "vkCreateGraphicsPipelines")) return 0;
    blendAtt.blendEnable = VK_TRUE;
    blendAtt.srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA;
    blendAtt.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blendAtt.colorBlendOp = VK_BLEND_OP_ADD;
    blendAtt.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    blendAtt.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blendAtt.alphaBlendOp = VK_BLEND_OP_ADD;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateGraphicsPipelines(g_fireGfxVk.device, NULL, 1, &gi, NULL, &g_fireGfxVk.imageBlend), "vkCreateGraphicsPipelines")) return 0;
    /* the atlas: one byte per pixel, its copy in host memory */
    memset(&ii, 0, sizeof ii);
    ii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = VK_FORMAT_R8_UNORM;
    ii.extent.width = FIRE_GFX_ATLAS_SIZE; ii.extent.height = FIRE_GFX_ATLAS_SIZE; ii.extent.depth = 1;
    ii.mipLevels = 1;
    ii.arrayLayers = 1;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_OPTIMAL;
    ii.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateImage(g_fireGfxVk.device, &ii, NULL, &g_fireGfxVk.atlas), "vkCreateImage")) return 0;
    g_fireGfxVk.vkGetImageMemoryRequirements(g_fireGfxVk.device, g_fireGfxVk.atlas, &req);
    type = fire_gfx_gpuMemoryType(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (type < 0) type = fire_gfx_gpuMemoryType(req.memoryTypeBits, 0);
    if (type < 0) { fire_gfx_gpuFail("The GPU has no memory for the glyphs."); return 0; }
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = (uint32_t)type;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkAllocateMemory(g_fireGfxVk.device, &ai, NULL, &g_fireGfxVk.atlasMemory), "vkAllocateMemory")
        || !fire_gfx_gpuCheck(g_fireGfxVk.vkBindImageMemory(g_fireGfxVk.device, g_fireGfxVk.atlas, g_fireGfxVk.atlasMemory, 0), "vkBindImageMemory")) return 0;
    memset(&vw, 0, sizeof vw);
    vw.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vw.image = g_fireGfxVk.atlas;
    vw.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vw.format = VK_FORMAT_R8_UNORM;
    vw.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vw.subresourceRange.levelCount = 1;
    vw.subresourceRange.layerCount = 1;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateImageView(g_fireGfxVk.device, &vw, NULL, &g_fireGfxVk.atlasView), "vkCreateImageView")) return 0;
    g_fireGfxVk.atlasSet = fire_gfx_gpuSetFor(g_fireGfxVk.atlasView);
    if (g_fireGfxVk.atlasSet == FIRE_GFX_NO_SET) { fire_gfx_gpuFail("vkAllocateDescriptorSets failed."); return 0; }
    if (!fire_gfx_gpuBuffer(&g_fireGfxVk.atlasMirror, (VkDeviceSize)FIRE_GFX_ATLAS_SIZE * FIRE_GFX_ATLAS_SIZE, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)) return 0;
    memset(g_fireGfxVk.atlasMirror.mapped, 0, (size_t)FIRE_GFX_ATLAS_SIZE * FIRE_GFX_ATLAS_SIZE);
    g_fireGfxVk.slots = (FireGfxAtlasSlot*)calloc(FIRE_GFX_ATLAS_SLOTS, sizeof(FireGfxAtlasSlot));
    if (g_fireGfxVk.slots == NULL) { fire_gfx_gpuFail("Out of memory."); return 0; }
    /* (all of it goes up the first time: the layout of the atlas is set then) */
    g_fireGfxVk.dirty[0] = 0; g_fireGfxVk.dirty[1] = 0; g_fireGfxVk.dirty[2] = FIRE_GFX_ATLAS_SIZE; g_fireGfxVk.dirty[3] = FIRE_GFX_ATLAS_SIZE;
    return fire_gfx_gpuBuffer(&g_fireGfxVk.images, sizeof(FireGfxGpuImage) * 1024, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT);
}

/* Vulkan once (1: ready). */
static int fire_gfx_gpuReady(void) {
    if (g_fireGfxVk.state != 0) return g_fireGfxVk.state > 0;
    g_fireGfxVk.state = -1;
    if (!fire_gfx_gpuLoad() || !fire_gfx_gpuInstance() || !fire_gfx_gpuPick() || !fire_gfx_gpuDevice() || !fire_gfx_gpuPipelines() || !fire_gfx_gpuImagePipelines()) return 0;
    if (!fire_gfx_gpuBuffer(&g_fireGfxVk.shapes, sizeof(FireGfxGpuShape) * 1024, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)) return 0;
    g_fireGfxVk.state = 1;
    return 1;
}

/* ---- the twin of a framebuffer ---- */

/* Takes the twin out of the list of those with drawing to do. */
static void fire_gfx_gpuUnlist(FireGfxGpuTwin* t) {
    FireGfxGpuTwin** at = &g_fireGfxVk.pending;
    if (!t->listed) return;
    while (*at != NULL && *at != t) at = &(*at)->nextPending;
    if (*at == t) *at = t->nextPending;
    t->nextPending = NULL; t->listed = 0;
}
static void fire_gfx_gpuList(FireGfxGpuTwin* t) {
    FireGfxGpuTwin** at = &g_fireGfxVk.pending;
    if (t->listed) return;
    while (*at != NULL) at = &(*at)->nextPending;
    *at = t; t->nextPending = NULL; t->listed = 1;
}

static void fire_gfx_gpuFreeTwin(FireGfxGpuTwin* t) {
    if (t == NULL) return;
    fire_gfx_gpuUnlist(t);
    if (g_fireGfxVk.device) {
        if (t->set) g_fireGfxVk.vkFreeDescriptorSets(g_fireGfxVk.device, g_fireGfxVk.descPool, 1, &t->set);
        if (t->framebuffer) g_fireGfxVk.vkDestroyFramebuffer(g_fireGfxVk.device, t->framebuffer, NULL);
        if (t->view) g_fireGfxVk.vkDestroyImageView(g_fireGfxVk.device, t->view, NULL);
        if (t->image) g_fireGfxVk.vkDestroyImage(g_fireGfxVk.device, t->image, NULL);
        if (t->memory) g_fireGfxVk.vkFreeMemory(g_fireGfxVk.device, t->memory, NULL);
        fire_gfx_gpuFreeBuffer(&t->staging);
    }
    free(t->shapes);
    free(t->images);
    free(t->batches);
    free(t);
}

static FireGfxGpuTwin* fire_gfx_gpuMakeTwin(int width, int height) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)calloc(1, sizeof(FireGfxGpuTwin));
    VkImageCreateInfo ii;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    VkImageViewCreateInfo vi;
    VkFramebufferCreateInfo fi;
    int type;
    if (t == NULL) return NULL;
    t->width = width; t->height = height;
    memset(&ii, 0, sizeof ii);
    ii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = VK_FORMAT_R8G8B8A8_UNORM;
    ii.extent.width = (uint32_t)width; ii.extent.height = (uint32_t)height; ii.extent.depth = 1;
    ii.mipLevels = 1;
    ii.arrayLayers = 1;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_OPTIMAL;
    ii.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateImage(g_fireGfxVk.device, &ii, NULL, &t->image), "vkCreateImage")) { fire_gfx_gpuFreeTwin(t); return NULL; }
    g_fireGfxVk.vkGetImageMemoryRequirements(g_fireGfxVk.device, t->image, &req);
    type = fire_gfx_gpuMemoryType(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (type < 0) type = fire_gfx_gpuMemoryType(req.memoryTypeBits, 0);
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = (uint32_t)(type < 0 ? 0 : type);
    if (type < 0 || !fire_gfx_gpuCheck(g_fireGfxVk.vkAllocateMemory(g_fireGfxVk.device, &ai, NULL, &t->memory), "vkAllocateMemory")
        || !fire_gfx_gpuCheck(g_fireGfxVk.vkBindImageMemory(g_fireGfxVk.device, t->image, t->memory, 0), "vkBindImageMemory")) { fire_gfx_gpuFreeTwin(t); return NULL; }
    memset(&vi, 0, sizeof vi);
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = t->image;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = VK_FORMAT_R8G8B8A8_UNORM;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = 1;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateImageView(g_fireGfxVk.device, &vi, NULL, &t->view), "vkCreateImageView")) { fire_gfx_gpuFreeTwin(t); return NULL; }
    memset(&fi, 0, sizeof fi);
    fi.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
    fi.renderPass = g_fireGfxVk.pass;
    fi.attachmentCount = 1;
    fi.pAttachments = &t->view;
    fi.width = (uint32_t)width; fi.height = (uint32_t)height; fi.layers = 1;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateFramebuffer(g_fireGfxVk.device, &fi, NULL, &t->framebuffer), "vkCreateFramebuffer")
        || !fire_gfx_gpuBuffer(&t->staging, (VkDeviceSize)width * (VkDeviceSize)height * 4, VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT)) { fire_gfx_gpuFreeTwin(t); return NULL; }
    t->set = fire_gfx_gpuSetFor(t->view);
    t->cpuValid = 1;   /* (the CPU holds the picture; it goes up before the GPU draws first) */
    return t;
}

/* A barrier on the image: everything before (transfer, drawing) is done and visible to everything after. */
static void fire_gfx_gpuBarrier(VkCommandBuffer cmd, VkImage image, VkImageLayout from) {
    VkImageMemoryBarrier b;
    memset(&b, 0, sizeof b);
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    b.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT | VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    b.oldLayout = from;
    b.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = image;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = 1;
    g_fireGfxVk.vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TRANSFER_BIT | VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                                     VK_PIPELINE_STAGE_TRANSFER_BIT | VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, 0, 0, NULL, 0, NULL, 1, &b);
}

/* Puts `data` (a run of `count` items of `size` bytes) into the host buffer `b`, which grows when needed. */
static int fire_gfx_gpuFill(FireGfxVkBuffer* b, const void* data, size_t size, uint32_t count, VkBufferUsageFlags usage) {
    VkDeviceSize need = (VkDeviceSize)size * count;
    if (need == 0) return 1;
    if (b->size < need) {
        VkDeviceSize grown = b->size ? b->size : 4096;
        while (grown < need) grown *= 2;
        fire_gfx_gpuFreeBuffer(b);
        if (!fire_gfx_gpuBuffer(b, grown, usage)) return 0;
    }
    memcpy(b->mapped, data, (size_t)need);
    return 1;
}

static int fire_gfx_gpuRunOne(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, int fetch);

/* Runs the drawing of the twins that came into the list before `until` (all of them: NULL), the oldest first. One that blits from `until` came before its newer drawing
 * and must draw from the picture before it; one that came after may hold drawing newer than `until`'s (a source drawn over after the blit) and waits. (A blit runs
 * its source when it is queued: a twin never waits for a source that is still in the list.) */
static int fire_gfx_gpuRunOthers(FireGfxGpuTwin* until) {
    FireGfxGpuTwin* t = g_fireGfxVk.pending;
    while (t != NULL && t != until) {
        FireGfxGpuTwin* next = t->nextPending;
        if (!fire_gfx_gpuRunOne(t->fb, t, 0)) return 0;
        t = next;
    }
    return 1;
}

/* Runs what the twin has queued (the drawing of the twins listed before it first): the upload of the CPU's picture first (when the CPU drew last), the new glyphs of the atlas,
 * the shapes and pictures in the order they were drawn, and - with `fetch` - the picture back to the CPU. 0: it failed. */
static int fire_gfx_gpuRun(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, int fetch) {
    if (!fire_gfx_gpuRunOthers(t)) return 0;
    return fire_gfx_gpuRunOne(fb, t, fetch);
}

static int fire_gfx_gpuRunOne(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, int fetch) {
    VkCommandBuffer cmd = g_fireGfxVk.cmd;
    VkCommandBufferBeginInfo bi;
    VkSubmitInfo si;
    VkBufferImageCopy region;
    VkMemoryBarrier mb;
    size_t bytes = (size_t)t->width * (size_t)t->height * 4;
    int upload = !t->gpuValid, draw = t->batchCount > 0, atlas = g_fireGfxVk.dirty[2] > g_fireGfxVk.dirty[0];
    fire_gfx_gpuUnlist(t);
    if (!upload && !draw && !(fetch && !t->cpuValid)) return 1;
    if (!fire_gfx_gpuFill(&g_fireGfxVk.shapes, t->shapes, sizeof(FireGfxGpuShape), t->shapeCount, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)
        || !fire_gfx_gpuFill(&g_fireGfxVk.images, t->images, sizeof(FireGfxGpuImage), t->imageCount, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)) return 0;
    if (upload) memcpy(t->staging.mapped, fb->pixels, bytes);
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    g_fireGfxVk.vkResetCommandBuffer(cmd, 0);
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkBeginCommandBuffer(cmd, &bi), "vkBeginCommandBuffer")) return 0;
    /* (what the runs before wrote - pictures that are sampled now - is visible) */
    memset(&mb, 0, sizeof mb);
    mb.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    mb.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    mb.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT | VK_ACCESS_TRANSFER_WRITE_BIT | VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    g_fireGfxVk.vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TRANSFER_BIT | VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                                     VK_PIPELINE_STAGE_TRANSFER_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT | VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, 0, 1, &mb, 0, NULL, 0, NULL);
    if (atlas) {
        VkBufferImageCopy ar;
        int* d = g_fireGfxVk.dirty;
        memset(&ar, 0, sizeof ar);
        ar.bufferOffset = (VkDeviceSize)d[1] * FIRE_GFX_ATLAS_SIZE + (VkDeviceSize)d[0];
        ar.bufferRowLength = FIRE_GFX_ATLAS_SIZE;
        ar.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        ar.imageSubresource.layerCount = 1;
        ar.imageOffset.x = d[0]; ar.imageOffset.y = d[1];
        ar.imageExtent.width = (uint32_t)(d[2] - d[0]); ar.imageExtent.height = (uint32_t)(d[3] - d[1]); ar.imageExtent.depth = 1;
        fire_gfx_gpuBarrier(cmd, g_fireGfxVk.atlas, g_fireGfxVk.atlasReady ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_UNDEFINED);
        g_fireGfxVk.vkCmdCopyBufferToImage(cmd, g_fireGfxVk.atlasMirror.buffer, g_fireGfxVk.atlas, VK_IMAGE_LAYOUT_GENERAL, 1, &ar);
        fire_gfx_gpuBarrier(cmd, g_fireGfxVk.atlas, VK_IMAGE_LAYOUT_GENERAL);
        g_fireGfxVk.atlasReady = 1;
        d[0] = d[1] = d[2] = d[3] = 0;
    }
    memset(&region, 0, sizeof region);
    region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    region.imageSubresource.layerCount = 1;
    region.imageExtent.width = (uint32_t)t->width; region.imageExtent.height = (uint32_t)t->height; region.imageExtent.depth = 1;
    if (upload) {
        fire_gfx_gpuBarrier(cmd, t->image, VK_IMAGE_LAYOUT_UNDEFINED);
        g_fireGfxVk.vkCmdCopyBufferToImage(cmd, t->staging.buffer, t->image, VK_IMAGE_LAYOUT_GENERAL, 1, &region);
        fire_gfx_gpuBarrier(cmd, t->image, VK_IMAGE_LAYOUT_GENERAL);
    }
    if (draw) {
        VkRenderPassBeginInfo rb;
        VkViewport viewport;
        VkDeviceSize offset = 0;
        float size[2];
        uint32_t k;
        memset(&rb, 0, sizeof rb);
        rb.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
        rb.renderPass = g_fireGfxVk.pass;
        rb.framebuffer = t->framebuffer;
        rb.renderArea.extent.width = (uint32_t)t->width; rb.renderArea.extent.height = (uint32_t)t->height;
        g_fireGfxVk.vkCmdBeginRenderPass(cmd, &rb, VK_SUBPASS_CONTENTS_INLINE);
        memset(&viewport, 0, sizeof viewport);
        viewport.width = (float)t->width; viewport.height = (float)t->height; viewport.maxDepth = 1.0f;
        g_fireGfxVk.vkCmdSetViewport(cmd, 0, 1, &viewport);
        size[0] = (float)t->width; size[1] = (float)t->height;
        for (k = 0; k < t->batchCount; k++) {
            const FireGfxGpuBatch* b = &t->batches[k];
            VkRect2D scissor;
            scissor.offset.x = b->clip[0]; scissor.offset.y = b->clip[1];
            scissor.extent.width = (uint32_t)(b->clip[2] - b->clip[0]); scissor.extent.height = (uint32_t)(b->clip[3] - b->clip[1]);
            if (b->kind == 0) {
                g_fireGfxVk.vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, b->copy ? g_fireGfxVk.copy : g_fireGfxVk.blend);
                g_fireGfxVk.vkCmdBindVertexBuffers(cmd, 0, 1, &g_fireGfxVk.shapes.buffer, &offset);
                g_fireGfxVk.vkCmdPushConstants(cmd, g_fireGfxVk.layout, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof size, size);
            } else {
                g_fireGfxVk.vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, b->copy ? g_fireGfxVk.imageCopy : g_fireGfxVk.imageBlend);
                g_fireGfxVk.vkCmdBindVertexBuffers(cmd, 0, 1, &g_fireGfxVk.images.buffer, &offset);
                g_fireGfxVk.vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_fireGfxVk.imageLayout, 0, 1, &b->set, 0, NULL);
                g_fireGfxVk.vkCmdPushConstants(cmd, g_fireGfxVk.imageLayout, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof size, size);
            }
            g_fireGfxVk.vkCmdSetScissor(cmd, 0, 1, &scissor);
            g_fireGfxVk.vkCmdDraw(cmd, 6, b->count, 0, b->first);
        }
        g_fireGfxVk.vkCmdEndRenderPass(cmd);
    }
    if (fetch) {
        fire_gfx_gpuBarrier(cmd, t->image, VK_IMAGE_LAYOUT_GENERAL);
        g_fireGfxVk.vkCmdCopyImageToBuffer(cmd, t->image, VK_IMAGE_LAYOUT_GENERAL, t->staging.buffer, 1, &region);
    }
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkEndCommandBuffer(cmd), "vkEndCommandBuffer")) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    g_fireGfxVk.vkResetFences(g_fireGfxVk.device, 1, &g_fireGfxVk.fence);
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkQueueSubmit(g_fireGfxVk.queue, 1, &si, g_fireGfxVk.fence), "vkQueueSubmit")) return 0;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkWaitForFences(g_fireGfxVk.device, 1, &g_fireGfxVk.fence, VK_TRUE, ~(uint64_t)0), "vkWaitForFences")) return 0;
    t->gpuValid = 1;
    t->shapeCount = 0; t->imageCount = 0; t->batchCount = 0;
    if (draw) t->cpuValid = 0;
    if (fetch) { memcpy(fb->pixels, t->staging.mapped, bytes); t->cpuValid = 1; }
    return 1;
}

/* The CPU is about to read (`writes` 0) or draw (1) the framebuffer: it gets the current pixels first; after a drawing of the CPU the GPU's picture is old. */
static void fire_gfx_gpuCpu(FireGfxFramebuffer* fb, int writes) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    if (t == NULL) return;
    if (t->width != fb->width || t->height != fb->height || fb->pixels == NULL) {
        /* resized (the CPU's picture is the new one) or no longer RGBA: the twin goes (what others still draw from it first) */
        fb->gpu = NULL;
        fire_gfx_gpuUnlist(t);
        fire_gfx_gpuRunOthers(NULL);
        fire_gfx_gpuFreeTwin(t);
        if (fb->pixels != NULL && fb->indices == NULL && fire_gfx_gpuReady()) fb->gpu = fire_gfx_gpuMakeTwin(fb->width, fb->height);
        if (fb->gpu != NULL) ((FireGfxGpuTwin*)fb->gpu)->fb = fb;
        return;
    }
    if (!t->cpuValid || t->batchCount > 0) {
        if (!fire_gfx_gpuRun(fb, t, 1)) { fb->gpu = NULL; fire_gfx_gpuFreeTwin(t); return; }
    }
    if (writes) t->gpuValid = 0;
}

/* (fire_gfx.h asks through these: it knows nothing of the GPU) */
static void fire_gfx_gpuSyncHook(FireGfxFramebuffer* fb, int writes) { fire_gfx_gpuCpu(fb, writes); }
/* (a framebuffer goes: what others still draw from it is drawn first) */
static void fire_gfx_gpuFreeHook(FireGfxFramebuffer* fb) { FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu; fb->gpu = NULL; fire_gfx_gpuUnlist(t); fire_gfx_gpuRunOthers(NULL); fire_gfx_gpuFreeTwin(t); }

static int fire_gfx_gpuAttach(FireGfxFramebuffer* fb) {
    if (fb->gpu != NULL) return 1;
    if (fb->indices != NULL || fb->pixels == NULL || !fire_gfx_gpuReady()) return 0;
    g_fireGfxCpuHook = fire_gfx_gpuSyncHook;
    g_fireGfxFreeHook = fire_gfx_gpuFreeHook;
    fb->gpu = fire_gfx_gpuMakeTwin(fb->width, fb->height);
    if (fb->gpu != NULL) ((FireGfxGpuTwin*)fb->gpu)->fb = fb;
    return fb->gpu != NULL;
}

static void fire_gfx_gpuDetach(FireGfxFramebuffer* fb) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    if (t == NULL) return;
    fire_gfx_gpuCpu(fb, 0);
    t = (FireGfxGpuTwin*)fb->gpu;
    fb->gpu = NULL;
    if (t != NULL) { fire_gfx_gpuUnlist(t); fire_gfx_gpuRunOthers(NULL); }
    fire_gfx_gpuFreeTwin(t);
}

/* The clip of the renderer within the picture. */
static int fire_gfx_gpuClip(const FireGfxGpuTwin* t, int* cl, int* ct, int* cr, int* cb) {
    if (*cl < 0) *cl = 0;
    if (*ct < 0) *ct = 0;
    if (*cr > t->width) *cr = t->width;
    if (*cb > t->height) *cb = t->height;
    return *cr > *cl && *cb > *ct;
}

/* The batch the next command of `kind` goes into: the last one when it fits, else a new one. NULL without memory. The twin has drawing to do from now on. */
static FireGfxGpuBatch* fire_gfx_gpuBatch(FireGfxGpuTwin* t, int kind, int cl, int ct, int cr, int cb, int copy, VkDescriptorSet set) {
    FireGfxGpuBatch* b = t->batchCount > 0 ? &t->batches[t->batchCount - 1] : NULL;
    if (b == NULL || b->kind != kind || b->copy != copy || b->set != set || b->clip[0] != cl || b->clip[1] != ct || b->clip[2] != cr || b->clip[3] != cb) {
        if (t->batchCount == t->batchCap) {
            uint32_t cap = t->batchCap ? t->batchCap * 2 : 16;
            FireGfxGpuBatch* grown = (FireGfxGpuBatch*)realloc(t->batches, sizeof(FireGfxGpuBatch) * cap);
            if (grown == NULL) return NULL;
            t->batches = grown; t->batchCap = cap;
        }
        b = &t->batches[t->batchCount++];
        b->kind = kind;
        b->clip[0] = cl; b->clip[1] = ct; b->clip[2] = cr; b->clip[3] = cb;
        b->copy = copy;
        b->set = set;
        b->first = kind == 0 ? t->shapeCount : t->imageCount; b->count = 0;
    }
    fire_gfx_gpuList(t);
    t->cpuValid = 0;
    return b;
}

/* A rounded rectangle (x, y, w, h; radius; `stroke` > 0: its outline that wide) in `rgba`, clipped to (cl, ct)-(cr, cb), blended - or copied (`copy`: from half
 * coverage on, the colour with its alpha). 0: the GPU does not draw it (no twin): the caller draws it in software. */
static int fire_gfx_gpuShape(FireGfxFramebuffer* fb, int cl, int ct, int cr, int cb, int x, int y, int w, int h, int radius, int stroke, uint32_t rgba, int copy) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    FireGfxGpuShape* s;
    FireGfxGpuBatch* b;
    int x0, y0, x1, y1;
    if (t == NULL || t->width != fb->width || t->height != fb->height) return 0;
    if (w <= 0 || h <= 0) return 1;
    if (stroke > 0 && (2 * stroke >= w || 2 * stroke >= h)) stroke = 0;   /* (as the software: an outline that fills it is a filling) */
    if (!fire_gfx_gpuClip(t, &cl, &ct, &cr, &cb)) return 1;
    x0 = x > cl ? x : cl; y0 = y > ct ? y : ct;
    x1 = x + w < cr ? x + w : cr; y1 = y + h < cb ? y + h : cb;
    if (x1 <= x0 || y1 <= y0) return 1;
    if (t->shapeCount == t->shapeCap) {
        uint32_t cap = t->shapeCap ? t->shapeCap * 2 : 256;
        FireGfxGpuShape* grown = (FireGfxGpuShape*)realloc(t->shapes, sizeof(FireGfxGpuShape) * cap);
        if (grown == NULL) return 0;
        t->shapes = grown; t->shapeCap = cap;
    }
    b = fire_gfx_gpuBatch(t, 0, cl, ct, cr, cb, copy, FIRE_GFX_NO_SET);
    if (b == NULL) return 0;
    s = &t->shapes[t->shapeCount++];
    s->quad[0] = (float)x0; s->quad[1] = (float)y0; s->quad[2] = (float)(x1 - x0); s->quad[3] = (float)(y1 - y0);
    s->box[0] = (float)x; s->box[1] = (float)y; s->box[2] = (float)w; s->box[3] = (float)h;
    s->param[0] = (float)(radius > 0 ? radius : 0); s->param[1] = (float)(stroke > 0 ? stroke : 0); s->param[2] = copy ? 1.0f : 0.0f; s->param[3] = 0.0f;
    s->color = rgba;
    b->count++;
    return 1;
}

/* A picture from the texture of `set` (a glyph of the atlas, or a framebuffer): the rectangle (dx, dy, dw, dh; negative: mirrored) gets (sx, sy, sw, sh) of it, nearest,
 * clipped to (cl, ct)-(cr, cb). `mode` FIRE_GFX_GPU_*; a glyph has the colour `rgba` and blends unless `copy`. 0 without memory. */
static int fire_gfx_gpuPicture(FireGfxGpuTwin* t, VkDescriptorSet set, int cl, int ct, int cr, int cb, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh,
                               int mode, uint32_t rgba, int copy) {
    FireGfxGpuImage* im;
    FireGfxGpuBatch* b;
    int aw = dw < 0 ? -dw : dw, ah = dh < 0 ? -dh : dh, x0, y0, x1, y1;
    if (sw <= 0 || sh <= 0 || aw == 0 || ah == 0) return 1;
    if (!fire_gfx_gpuClip(t, &cl, &ct, &cr, &cb)) return 1;
    x0 = dx > cl ? dx : cl; y0 = dy > ct ? dy : ct;
    x1 = dx + aw < cr ? dx + aw : cr; y1 = dy + ah < cb ? dy + ah : cb;
    if (x1 <= x0 || y1 <= y0) return 1;
    if (t->imageCount == t->imageCap) {
        uint32_t cap = t->imageCap ? t->imageCap * 2 : 256;
        FireGfxGpuImage* grown = (FireGfxGpuImage*)realloc(t->images, sizeof(FireGfxGpuImage) * cap);
        if (grown == NULL) return 0;
        t->images = grown; t->imageCap = cap;
    }
    b = fire_gfx_gpuBatch(t, 1, cl, ct, cr, cb, copy, set);
    if (b == NULL) return 0;
    im = &t->images[t->imageCount++];
    im->quad[0] = (float)x0; im->quad[1] = (float)y0; im->quad[2] = (float)(x1 - x0); im->quad[3] = (float)(y1 - y0);
    im->dst[0] = dx; im->dst[1] = dy; im->dst[2] = dw; im->dst[3] = dh;
    im->src[0] = sx; im->src[1] = sy; im->src[2] = sw; im->src[3] = sh;
    im->mode[0] = mode; im->mode[1] = im->mode[2] = im->mode[3] = 0;
    im->color = rgba;
    b->count++;
    return 1;
}

/* ---- the atlas of the glyphs ---- */

static uint64_t fire_gfx_gpuHash(uint64_t k) { k ^= k >> 33; k *= 0xff51afd7ed558ccdULL; k ^= k >> 33; k *= 0xc4ceb9fe1a85ec53ULL; k ^= k >> 33; return k; }

/* Starts the atlas afresh (it is full): what is queued still draws from it first. */
static int fire_gfx_gpuAtlasReset(void) {
    if (!fire_gfx_gpuRunOthers(NULL)) return 0;
    memset(g_fireGfxVk.slots, 0, sizeof(FireGfxAtlasSlot) * FIRE_GFX_ATLAS_SLOTS);
    g_fireGfxVk.slotCount = 0;
    g_fireGfxVk.shelfX = g_fireGfxVk.shelfY = g_fireGfxVk.shelfH = 0;
    return 1;
}

/* The place of glyph `key` (w x h) in the atlas: found, or made from `coverage` (w*h bytes; NULL: from the 8 bits of each row of `rows`). 0: it does not fit at all. */
static int fire_gfx_gpuAtlasGlyph(uint64_t key, int w, int h, const uint8_t* coverage, const uint8_t* rows, int* ax, int* ay) {
    uint64_t probe;
    FireGfxAtlasSlot* slot;
    int tries, gy, gx;
    if (w <= 0 || h <= 0 || w > FIRE_GFX_ATLAS_SIZE || h > 256) return 0;
    key = (key << 1) | 1;   /* (0 marks a free slot; the shift keeps neighbouring keys apart) */
    for (tries = 0; tries < 2; tries++) {
        probe = fire_gfx_gpuHash(key);
        for (;;) {
            slot = &g_fireGfxVk.slots[probe & (FIRE_GFX_ATLAS_SLOTS - 1)];
            if (slot->key == key) { *ax = slot->x; *ay = slot->y; return 1; }
            if (slot->key == 0) break;
            probe++;
        }
        /* a new one: on the shelf, else on a new shelf; a full atlas (or too many glyphs) starts afresh */
        if (g_fireGfxVk.shelfX + w > FIRE_GFX_ATLAS_SIZE) { g_fireGfxVk.shelfY += g_fireGfxVk.shelfH + 1; g_fireGfxVk.shelfX = 0; g_fireGfxVk.shelfH = 0; }
        if (g_fireGfxVk.shelfY + h > FIRE_GFX_ATLAS_SIZE || g_fireGfxVk.slotCount >= FIRE_GFX_ATLAS_SLOTS / 2) {
            if (tries == 1 || !fire_gfx_gpuAtlasReset()) return 0;
            continue;
        }
        slot->key = key; slot->x = g_fireGfxVk.shelfX; slot->y = g_fireGfxVk.shelfY; slot->w = w; slot->h = h;
        g_fireGfxVk.slotCount++;
        g_fireGfxVk.shelfX += w + 1;
        if (h > g_fireGfxVk.shelfH) g_fireGfxVk.shelfH = h;
        for (gy = 0; gy < h; gy++) {
            uint8_t* dst = (uint8_t*)g_fireGfxVk.atlasMirror.mapped + (size_t)(slot->y + gy) * FIRE_GFX_ATLAS_SIZE + (size_t)slot->x;
            if (coverage != NULL) memcpy(dst, coverage + (size_t)gy * w, (size_t)w);
            else for (gx = 0; gx < w; gx++) dst[gx] = ((rows[gy] >> (7 - gx)) & 1u) ? 255 : 0;
        }
        /* (what goes up at the next run) */
        if (g_fireGfxVk.dirty[2] <= g_fireGfxVk.dirty[0]) { g_fireGfxVk.dirty[0] = slot->x; g_fireGfxVk.dirty[1] = slot->y; g_fireGfxVk.dirty[2] = slot->x + w; g_fireGfxVk.dirty[3] = slot->y + h; }
        else {
            if (slot->x < g_fireGfxVk.dirty[0]) g_fireGfxVk.dirty[0] = slot->x;
            if (slot->y < g_fireGfxVk.dirty[1]) g_fireGfxVk.dirty[1] = slot->y;
            if (slot->x + w > g_fireGfxVk.dirty[2]) g_fireGfxVk.dirty[2] = slot->x + w;
            if (slot->y + h > g_fireGfxVk.dirty[3]) g_fireGfxVk.dirty[3] = slot->y + h;
        }
        *ax = slot->x; *ay = slot->y;
        return 1;
    }
    return 0;
}

/* A glyph at (x, y): its coverage times `rgba` (blended; `copy`: copied where it covers - as fire_gfx_put without blending). 0: the GPU cannot. */
static int fire_gfx_gpuGlyph(FireGfxGpuTwin* t, const int* clip, uint64_t key, int x, int y, int w, int h, const uint8_t* coverage, const uint8_t* rows, uint32_t rgba, int copy) {
    int ax, ay;
    if (!fire_gfx_gpuAtlasGlyph(key, w, h, coverage, rows, &ax, &ay)) return 0;
    return fire_gfx_gpuPicture(t, g_fireGfxVk.atlasSet, clip[0], clip[1], clip[2], clip[3], ax, ay, w, h, x, y, w, h, FIRE_GFX_GPU_GLYPH, rgba, copy);
}

/* ---- text ---- */

/* (what the glyphs of a TrueType text go to the GPU with) */
typedef struct FireGfxGpuText { FireGfxGpuTwin* t; const int* clip; uint64_t font; int x, base; uint32_t rgba; int copy, ok; } FireGfxGpuText;
static void fire_gfx_gpuTtfGlyph(void* ctx, const FireGfxGlyph* g, int penX) {
    FireGfxGpuText* d = (FireGfxGpuText*)ctx;
    if (!d->ok || !g->hasImage || g->m.minWidth <= 0 || g->m.minHeight <= 0) return;
    if (!fire_gfx_gpuGlyph(d->t, d->clip, fire_gfx_gpuHash(d->font ^ (uint64_t)g->key * 0x9E3779B97F4A7C15ULL), d->x + penX + g->m.xOffset, d->base + g->m.yOffset,
                           g->m.minWidth, g->m.minHeight, g->image, NULL, d->rgba, d->copy)) d->ok = 0;
}

/* Text on the GPU, as fire_gfx_drawTextIn and fire_gfx_rendererDrawText draw it: `font` NULL - the bitmap font of the renderer in its cells -, a built-in bitmap font or
 * TrueType at `size`; the colours already resolved (blended unless `copy`), `hasBg`: the cells / the line below in `bg`. 0: the GPU cannot (the caller draws it in software;
 * nothing was drawn when it says so before the first glyph - a glyph that does not fit in the atlas at all leaves the rest to the software as well). */
static int fire_gfx_gpuText(FireGfxFramebuffer* fb, const int* clipIn, int copy, FireGfxTextFont* font, int small, int cellW, int cellH, int size, int x, int y,
                            const uint16_t* text, uint32_t n, uint32_t fg, int hasBg, uint32_t bg) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    int clip[4], fgVisible;
    uint32_t i;
    if (t == NULL || t->width != fb->width || t->height != fb->height) return 0;
    clip[0] = clipIn[0]; clip[1] = clipIn[1]; clip[2] = clipIn[2]; clip[3] = clipIn[3];
    if (hasBg && !copy && (bg >> 24) == 0) hasBg = 0;
    fgVisible = copy || (fg >> 24) != 0;
    if (!fgVisible && !hasBg) return 1;
    if (font == NULL || font->bitmap) {
        /* a bitmap font: per character its cell (the background) and its bits */
        int sm = font == NULL ? small : font->smallBitmap;
        int cw = font == NULL ? cellW : fire_gfx_fontCellWidth(font), ch = font == NULL ? cellH : fire_gfx_fontCellHeight(font);
        for (i = 0; i < n; i++) {
            uint32_t c = text[i] < 256 ? text[i] : '?';
            const uint8_t* rows = sm ? g_fireGfxGlyphs8x8[c] : g_fireGfxGlyphs8x14[c];
            if (hasBg && !fire_gfx_gpuShape(fb, clip[0], clip[1], clip[2], clip[3], x, y, cw, ch, 0, 0, bg, copy)) return 0;
            if (fgVisible && !fire_gfx_gpuGlyph(t, clip, ((uint64_t)(sm ? 1 : 2) << 32) | c, x, y, cw, ch, NULL, rows, fg, copy)) return 0;
            x += cw;
        }
        return 1;
    }
    {
        FireGfxGpuText d;
        size = fire_gfx_clampFontSize(size);
        if (hasBg && !fire_gfx_gpuShape(fb, clip[0], clip[1], clip[2], clip[3], x, y, fire_gfx_fontMeasure(font, text, n, size), fire_gfx_fontLineHeight(font, size), 0, 0, bg, copy)) return 0;
        if (!fgVisible) return 1;
        d.t = t; d.clip = clip; d.font = (uint64_t)(uintptr_t)font * 31u + (uint64_t)size; d.x = x; d.base = y + fire_gfx_fontBaseline(font, size); d.rgba = fg; d.copy = copy; d.ok = 1;
        fire_gfx_fontLayout(font, text, n, size, fire_gfx_gpuTtfGlyph, &d);
        return d.ok;
    }
}

/* ---- blits ---- */

/* fire_gfx_blit on the GPU: an RGBA source (its own twin - made now, its picture uploaded now: the blit draws what it is now) into the twin of `dst`. Not: a palette source,
 * the same framebuffer, no twin - 0, the caller blits in software. */
static int fire_gfx_gpuBlit(FireGfxFramebuffer* dst, const FireGfxFramebuffer* srcIn, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode, int blend,
                            int cl, int ct, int cr, int cb) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)dst->gpu;
    FireGfxFramebuffer* src = (FireGfxFramebuffer*)srcIn;
    FireGfxGpuTwin* st;
    int gpuMode;
    if (t == NULL || t->width != dst->width || t->height != dst->height || src == dst || src->pixels == NULL || src->indices != NULL) return 0;
    if (sw <= 0 || sh <= 0 || dw == 0 || dh == 0) return 1;
    if (src->gpu == NULL && !fire_gfx_gpuAttach(src)) return 0;
    st = (FireGfxGpuTwin*)src->gpu;
    if (st == NULL || st->width != src->width || st->height != src->height || st->set == FIRE_GFX_NO_SET) return 0;
    /* the source as it is now: its own drawing and what draws from its old picture run first, then it goes up */
    if (!st->gpuValid || st->batchCount > 0) {
        if (!fire_gfx_gpuRun(src, st, 0)) return 0;
    }
    if (!blend && mode == FIRE_GFX_BLIT_BLEND) mode = FIRE_GFX_BLIT_TRANSPARENT;
    gpuMode = mode == FIRE_GFX_BLIT_COPY ? FIRE_GFX_GPU_COPY : mode == FIRE_GFX_BLIT_BLEND ? FIRE_GFX_GPU_BLEND : FIRE_GFX_GPU_TRANSPARENT;
    return fire_gfx_gpuPicture(t, st->set, cl, ct, cr, cb, sx, sy, sw, sh, dx, dy, dw, dh, gpuMode, 0, gpuMode != FIRE_GFX_GPU_BLEND);
}

#endif
#endif
