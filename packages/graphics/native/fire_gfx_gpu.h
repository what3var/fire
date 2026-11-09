/* fire graphics: the GPU backend (docs/spec/UI_PLATFORM.md, part 4) - a framebuffer may also live on the GPU (Vulkan, loaded at run time). Drawing that the GPU does
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
#include "fire_gfx_fx.h"

#if defined(ESP_PLATFORM) || defined(__EMSCRIPTEN__) || defined(FIRE_GFX_NO_GPU) || defined(FIRE_PLATFORM_FREERTOS_H)

static fire_gfx_int fire_gfx_gpuAttach(FireGfxFramebuffer* fb) { (void)fb; return 0; }
static void fire_gfx_gpuDetach(FireGfxFramebuffer* fb) { (void)fb; }
static fire_gfx_int fire_gfx_gpuShape(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int radius, fire_gfx_int stroke, uint32_t rgba, fire_gfx_int copy) {
    (void)fb; (void)cl; (void)ct; (void)cr; (void)cb; (void)x; (void)y; (void)w; (void)h; (void)radius; (void)stroke; (void)rgba; (void)copy;
    return 0;
}
static const char* fire_gfx_gpuError(void) { return "There is no GPU backend in this build."; }
static fire_gfx_int fire_gfx_gpuWanted(void) { return 0; }
static fire_gfx_int fire_gfx_gpuText(FireGfxFramebuffer* fb, const fire_gfx_int* clip, fire_gfx_int copy, FireGfxTextFont* font, fire_gfx_int small, fire_gfx_int cellW, fire_gfx_int cellH, fire_gfx_int size, fire_gfx_int x, fire_gfx_int y,
                            const uint16_t* text, uint32_t n, uint32_t fg, fire_gfx_int hasBg, uint32_t bg) {
    (void)fb; (void)clip; (void)copy; (void)font; (void)small; (void)cellW; (void)cellH; (void)size; (void)x; (void)y; (void)text; (void)n; (void)fg; (void)hasBg; (void)bg;
    return 0;
}
static fire_gfx_int fire_gfx_gpuBlit(FireGfxFramebuffer* dst, const FireGfxFramebuffer* src, fire_gfx_int sx, fire_gfx_int sy, fire_gfx_int sw, fire_gfx_int sh, fire_gfx_int dx, fire_gfx_int dy, fire_gfx_int dw, fire_gfx_int dh, fire_gfx_int mode, fire_gfx_int blend,
                            fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb) {
    (void)dst; (void)src; (void)sx; (void)sy; (void)sw; (void)sh; (void)dx; (void)dy; (void)dw; (void)dh; (void)mode; (void)blend; (void)cl; (void)ct; (void)cr; (void)cb;
    return 0;
}
static fire_gfx_int fire_gfx_gpuShadow(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int blend, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int radius, fire_gfx_int soft, uint32_t color, fire_gfx_int dy) {
    (void)fb; (void)cl; (void)ct; (void)cr; (void)cb; (void)blend; (void)x; (void)y; (void)w; (void)h; (void)radius; (void)soft; (void)color; (void)dy;
    return 0;
}
static fire_gfx_int fire_gfx_gpuBlur(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int corner, fire_gfx_int radius) {
    (void)fb; (void)cl; (void)ct; (void)cr; (void)cb; (void)x; (void)y; (void)w; (void)h; (void)corner; (void)radius;
    return 0;
}
static fire_gfx_int fire_gfx_gpuMaterial(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int blend, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int corner, fire_gfx_int kind, uint32_t tint, uint32_t base, fire_gfx_int flags) {
    (void)fb; (void)cl; (void)ct; (void)cr; (void)cb; (void)blend; (void)x; (void)y; (void)w; (void)h; (void)corner; (void)kind; (void)tint; (void)base; (void)flags;
    return 0;
}
static fire_gfx_int fire_gfx_gpuPublish(FireGfxFramebuffer* fb, fire_gfx_int transparent, void (**fn)(void), void** ctx) { (void)fb; (void)transparent; *fn = NULL; *ctx = NULL; return 1; }
static uint32_t fire_gfx_gpuReadbacks(void) { return 0; }

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
#  include <pthread.h>
#endif
#ifndef FIRE_VK_DECLS_H
#include "fire_gfx_vk.h"   /* (the same file as scene3d/native/s3d_vk.h: a native build with both has them once) */
#endif
#include "fire_gfx_shaders.h"

#define FIRE_GFX_NO_SET ((VkDescriptorSet)0)   /* (no descriptor set: VK_NULL_HANDLE) */

#define FIRE_GFX_VK_INSTANCE_FUNCTIONS(X) \
    X(vkDestroyInstance) X(vkEnumeratePhysicalDevices) X(vkGetPhysicalDeviceProperties) X(vkGetPhysicalDeviceQueueFamilyProperties) \
    X(vkGetPhysicalDeviceMemoryProperties) X(vkGetPhysicalDeviceFormatProperties) X(vkCreateDevice) X(vkGetDeviceProcAddr) X(vkEnumerateDeviceExtensionProperties)
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
    X(vkFreeDescriptorSets) X(vkUpdateDescriptorSets) X(vkCreateSampler) X(vkDestroySampler) X(vkCmdBindDescriptorSets) X(vkCreateSemaphore) X(vkDestroySemaphore) \
    X(vkCmdBlitImage) X(vkCmdCopyImage)
/* For the windows (part 4.4: the picture straight into a window), where the driver has them (VK_KHR_surface, VK_KHR_swapchain): loaded when they are there. */
#define FIRE_GFX_VK_WSI_INSTANCE_FUNCTIONS(X) \
    X(vkDestroySurfaceKHR) X(vkGetPhysicalDeviceSurfaceSupportKHR) X(vkGetPhysicalDeviceSurfaceCapabilitiesKHR) X(vkGetPhysicalDeviceSurfaceFormatsKHR) \
    X(vkGetPhysicalDeviceSurfacePresentModesKHR)
/* (the LUID of the device, for DirectComposition: Vulkan 1.1, or VK_KHR_get_physical_device_properties2 - loaded with either name) */
#define FIRE_GFX_VK_INTEROP_FUNCTIONS(X) X(vkGetPhysicalDeviceProperties2)
#define FIRE_GFX_VK_WSI_DEVICE_FUNCTIONS(X) \
    X(vkCreateSwapchainKHR) X(vkDestroySwapchainKHR) X(vkGetSwapchainImagesKHR) X(vkAcquireNextImageKHR) X(vkQueuePresentKHR)

/* The window of a presenter (the same declarations as in native/abi/fire_pkg_abi.h, where they are described). */
#ifndef FIRE_NATIVE_WINDOW_DEFINED
#define FIRE_NATIVE_WINDOW_DEFINED 1
enum { FIRE_NATIVE_NONE = 0, FIRE_NATIVE_WIN32 = 1, FIRE_NATIVE_X11 = 2, FIRE_NATIVE_WAYLAND = 3, FIRE_NATIVE_METAL = 4, FIRE_NATIVE_DCOMP = 5 };
typedef struct fire_native_window { int32_t kind; int32_t width, height; int32_t vsync; void* a; void* b; uint64_t id; } fire_native_window;
typedef int32_t (*fire_present_fn)(void* ctx, int32_t op, const fire_native_window* window, const uint32_t* pixels, int32_t width, int32_t height);
typedef struct fire_dcomp_bridge {
    int32_t size;
    uint32_t luidLow; int32_t luidHigh;
    int32_t premultiplied;
    void* (*texture)(struct fire_dcomp_bridge* bridge, int32_t width, int32_t height);
    int32_t (*present)(struct fire_dcomp_bridge* bridge);
} fire_dcomp_bridge;
#endif
/* The surfaces of the window systems (their declarations are in the platform headers of Vulkan, which need the headers of the window systems: written out here, as in
 * scene3d's s3d_gpu_vk.h). */
typedef struct FireGfxVkWin32SurfaceInfo { VkStructureType sType; const void* pNext; VkFlags flags; void* hinstance; void* hwnd; } FireGfxVkWin32SurfaceInfo;
typedef struct FireGfxVkXlibSurfaceInfo { VkStructureType sType; const void* pNext; VkFlags flags; void* dpy; unsigned long window; } FireGfxVkXlibSurfaceInfo;
typedef struct FireGfxVkWaylandSurfaceInfo { VkStructureType sType; const void* pNext; VkFlags flags; void* display; void* surface; } FireGfxVkWaylandSurfaceInfo;
typedef struct FireGfxVkMetalSurfaceInfo { VkStructureType sType; const void* pNext; VkFlags flags; const void* pLayer; } FireGfxVkMetalSurfaceInfo;
typedef VkResult (VKAPI_PTR *PFN_fireGfxCreateSurface)(VkInstance instance, const void* createInfo, const VkAllocationCallbacks* allocator, VkSurfaceKHR* surface);
/* Windows, DirectComposition (FIRE_NATIVE_DCOMP): the window's picture is a texture of D3D11, imported (VK_KHR_external_memory_win32; vulkan_win32.h's structures,
 * written out). */
typedef struct FireGfxVkImportWin32 { VkStructureType sType; const void* pNext; VkExternalMemoryHandleTypeFlagBits handleType; void* handle; const void* name; } FireGfxVkImportWin32;
typedef struct FireGfxVkWin32Props { VkStructureType sType; void* pNext; uint32_t memoryTypeBits; } FireGfxVkWin32Props;
typedef VkResult (VKAPI_PTR *PFN_fireGfxWin32HandleProps)(VkDevice device, VkExternalMemoryHandleTypeFlagBits type, void* handle, FireGfxVkWin32Props* props);

/* One lock for the backend: the program's thread draws, the windows' threads present, and both use the one queue. */
#ifdef _WIN32
static SRWLOCK g_fireGfxGpuLock = SRWLOCK_INIT;
static void fire_gfx_gpuLock(void) { AcquireSRWLockExclusive(&g_fireGfxGpuLock); }
static void fire_gfx_gpuUnlock(void) { ReleaseSRWLockExclusive(&g_fireGfxGpuLock); }
#else
static pthread_mutex_t g_fireGfxGpuLock = PTHREAD_MUTEX_INITIALIZER;
static void fire_gfx_gpuLock(void) { pthread_mutex_lock(&g_fireGfxGpuLock); }
static void fire_gfx_gpuUnlock(void) { pthread_mutex_unlock(&g_fireGfxGpuLock); }
#endif

/* One shape for the GPU (the vertex input of shaders/gfx_shape.vert): 52 bytes. */
typedef struct FireGfxGpuShape { float quad[4], box[4], param[4]; uint32_t color; } FireGfxGpuShape;
/* One picture for the GPU (the vertex input of shaders/gfx_image.vert): a glyph of the atlas or a blit - 68 bytes. */
typedef struct FireGfxGpuImage { float quad[4]; int32_t dst[4], src[4], mode[4]; uint32_t color; } FireGfxGpuImage;
enum { FIRE_GFX_GPU_GLYPH = 0, FIRE_GFX_GPU_COPY = 1, FIRE_GFX_GPU_TRANSPARENT = 2, FIRE_GFX_GPU_BLEND = 3 };
/* A blur or a material in a rounded rectangle (fire_gfx_fxBlurMaterial): its passes run between the other drawing (kind 2 of a batch). */
typedef struct FireGfxGpuEffect { fire_gfx_int clip[4]; fire_gfx_int x, y, w, h, corner, radius, kind, rim; uint32_t tint; } FireGfxGpuEffect;
/* The push constants of shaders/gfx_effect.* (96 bytes). */
typedef struct FireGfxGpuPass { float size[2]; int32_t mode, f; int32_t rect[4], region[4], box[4], p[4], q[4]; } FireGfxGpuPass;
/* A temporary picture of the effects (the smaller picture and its blur). */
typedef struct FireGfxGpuTemp { fire_gfx_int width, height, ready; VkImage image; VkDeviceMemory memory; VkImageView view; VkFramebuffer framebuffer; VkDescriptorSet set; } FireGfxGpuTemp;
/* A run of shapes (kind 0), pictures (kind 1, from the texture of `set`) or one effect (kind 2) with the same clip and pipeline - in the order they were drawn. */
typedef struct FireGfxGpuBatch { fire_gfx_int kind; fire_gfx_int clip[4]; fire_gfx_int copy; VkDescriptorSet set; uint32_t first, count; } FireGfxGpuBatch;

typedef struct FireGfxVkBuffer { VkBuffer buffer; VkDeviceMemory memory; void* mapped; VkDeviceSize size; } FireGfxVkBuffer;

/* The twin of a framebuffer on the GPU. */
typedef struct FireGfxGpuTwin {
    fire_gfx_int width, height;
    VkImage image; VkDeviceMemory memory; VkImageView view; VkFramebuffer framebuffer;
    FireGfxVkBuffer staging;          /* host memory of the size of the picture: uploads and readbacks */
    fire_gfx_int cpuValid, gpuValid;           /* who holds the current pixels (both: the same) */
    fire_gfx_int dirty[4];                     /* x0, y0, x1, y1: what the GPU drew since the pixels of the CPU were last the same (x1 <= x0: nothing) - only that comes back */
    FireGfxGpuShape* shapes; uint32_t shapeCount, shapeCap;
    FireGfxGpuImage* images; uint32_t imageCount, imageCap;
    FireGfxGpuEffect* effects; uint32_t effectCount, effectCap;
    FireGfxGpuBatch* batches; uint32_t batchCount, batchCap;
    VkDescriptorSet set;              /* the picture as a texture (a blit from this framebuffer into another) */
    FireGfxFramebuffer* fb;           /* whose twin it is */
    struct FireGfxGpuTwin* nextPending; fire_gfx_int listed;   /* in the list of the twins with drawing to do (in the order of their first command) */
} FireGfxGpuTwin;

/* The glyphs on the GPU: one texture (one byte of coverage per pixel), filled in shelves; a copy in host memory (`mirror`) goes up where it changed. */
#define FIRE_GFX_ATLAS_SIZE 2048
#define FIRE_GFX_ATLAS_SLOTS 8192
typedef struct FireGfxAtlasSlot { uint64_t key; fire_gfx_int x, y, w, h; } FireGfxAtlasSlot;

static struct {
    fire_gfx_int state;                 /* 0: not tried yet, 1: ready, -1: there is none (error says why) */
    char error[256];
    char deviceName[256];
    void* lib;
    PFN_vkGetInstanceProcAddr vkGetInstanceProcAddr;
    PFN_vkCreateInstance vkCreateInstance;
    PFN_vkEnumerateInstanceExtensionProperties vkEnumerateInstanceExtensionProperties;
#define FIRE_GFX_VK_DECLARE(name) PFN_##name name;
    FIRE_GFX_VK_INSTANCE_FUNCTIONS(FIRE_GFX_VK_DECLARE)
    FIRE_GFX_VK_DEVICE_FUNCTIONS(FIRE_GFX_VK_DECLARE)
    FIRE_GFX_VK_WSI_INSTANCE_FUNCTIONS(FIRE_GFX_VK_DECLARE)
    FIRE_GFX_VK_WSI_DEVICE_FUNCTIONS(FIRE_GFX_VK_DECLARE)
    FIRE_GFX_VK_INTEROP_FUNCTIONS(FIRE_GFX_VK_DECLARE)
#undef FIRE_GFX_VK_DECLARE
    fire_gfx_int interop;               /* Windows: the device can import textures of D3D11 (DirectComposition gets the frames without a readback) */
    uint8_t luid[8]; fire_gfx_int luidValid;
    PFN_fireGfxWin32HandleProps getWin32HandleProps;
    uint32_t readbacks;        /* how often pictures came back to the CPU (Framebuffer.GpuReadbacks: the tests) */
    fire_gfx_int wsi;                   /* the instance has VK_KHR_surface (and a surface of a window system), the device VK_KHR_swapchain: pictures can go into windows */
    PFN_fireGfxCreateSurface createWin32Surface, createXlibSurface, createWaylandSurface, createMetalSurface;
    struct FireGfxGpuTarget* targets;   /* the presenters of the framebuffers (part 4.4); never freed while the backend lives - a window may hold one */
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
    fire_gfx_int atlasReady;            /* its layout is GENERAL (set by its first upload) */
    fire_gfx_int shelfX, shelfY, shelfH;
    fire_gfx_int dirty[4];              /* x0, y0, x1, y1 of what changed in the mirror (x1 <= x0: nothing) */
    FireGfxAtlasSlot* slots; fire_gfx_int slotCount;
    FireGfxGpuTwin* pending;   /* the twins with drawing to do, the oldest first */
    /* blur and materials */
    VkShaderModule effectVert, effectFrag;
    VkPipelineLayout effectLayout;
    VkPipeline effectCopy, effectBlend;
    FireGfxGpuTemp temps[2];
} g_fireGfxVk;

static const char* fire_gfx_gpuError(void) { return g_fireGfxVk.state > 0 ? "" : g_fireGfxVk.error[0] ? g_fireGfxVk.error : "There is no GPU (not tried yet: set Accelerated first)."; }
static void fire_gfx_gpuFail(const char* text) { snprintf(g_fireGfxVk.error, sizeof g_fireGfxVk.error, "%s", text); }
static fire_gfx_int fire_gfx_gpuCheck(VkResult r, const char* what) {
    if (r == VK_SUCCESS) return 1;
    snprintf(g_fireGfxVk.error, sizeof g_fireGfxVk.error, "%s failed (VkResult %d).", what, (fire_gfx_int)r);
    return 0;
}

/* FIRE_GFX_GPU=1 (or cpu): every RGBA framebuffer is accelerated. */
static fire_gfx_int fire_gfx_gpuWanted(void) {
    const char* v = getenv("FIRE_GFX_GPU");
    return v != NULL && (strcmp(v, "1") == 0 || strcmp(v, "cpu") == 0);
}

/* ---- Vulkan ---- */

static fire_gfx_int fire_gfx_gpuLoad(void) {
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
    fire_gfx_int i;
    void* sym;
    for (i = 0; names[i] != NULL && g_fireGfxVk.lib == NULL; i++) g_fireGfxVk.lib = dlopen(names[i], RTLD_NOW | RTLD_LOCAL);
    if (g_fireGfxVk.lib == NULL) { fire_gfx_gpuFail("There is no Vulkan on this machine (the Vulkan loader was not found)."); return 0; }
    sym = dlsym(g_fireGfxVk.lib, "vkGetInstanceProcAddr");
    memcpy(&g_fireGfxVk.vkGetInstanceProcAddr, &sym, sizeof sym);
#endif
    if (g_fireGfxVk.vkGetInstanceProcAddr == NULL) { fire_gfx_gpuFail("The Vulkan loader has no vkGetInstanceProcAddr."); return 0; }
    g_fireGfxVk.vkCreateInstance = (PFN_vkCreateInstance)g_fireGfxVk.vkGetInstanceProcAddr(NULL, "vkCreateInstance");
    g_fireGfxVk.vkEnumerateInstanceExtensionProperties = (PFN_vkEnumerateInstanceExtensionProperties)g_fireGfxVk.vkGetInstanceProcAddr(NULL, "vkEnumerateInstanceExtensionProperties");
    if (g_fireGfxVk.vkCreateInstance == NULL) { fire_gfx_gpuFail("The Vulkan loader is incomplete."); return 0; }
    return 1;
}

static fire_gfx_int fire_gfx_gpuHasExtension(const VkExtensionProperties* list, uint32_t count, const char* name) {
    uint32_t i;
    for (i = 0; i < count; i++) if (strcmp(list[i].extensionName, name) == 0) return 1;
    return 0;
}

static fire_gfx_int fire_gfx_gpuInstance(void) {
    VkApplicationInfo app;
    VkInstanceCreateInfo ci;
    VkResult r;
    VkExtensionProperties* exts;
    const char* enabled[12];
    uint32_t n = 0, count = 0;
    fire_gfx_int surface = 0;
    memset(&app, 0, sizeof app);
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName = "fire graphics";
    app.pEngineName = "fire graphics";
    app.apiVersion = (uint32_t)1 << 22;   /* Vulkan 1.0 */
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ci.pApplicationInfo = &app;
    if (g_fireGfxVk.vkEnumerateInstanceExtensionProperties != NULL && g_fireGfxVk.vkEnumerateInstanceExtensionProperties(NULL, &count, NULL) == VK_SUCCESS && count > 0) {
        exts = (VkExtensionProperties*)malloc(sizeof(VkExtensionProperties) * count);
        if (exts && g_fireGfxVk.vkEnumerateInstanceExtensionProperties(NULL, &count, exts) == VK_SUCCESS) {
            static const char* const surfaces[4] = {"VK_KHR_win32_surface", "VK_KHR_xlib_surface", "VK_KHR_wayland_surface", "VK_EXT_metal_surface"};
            fire_gfx_int k;
            /* MoltenVK (macOS) is a "portability" implementation: it is only listed when the program says it can take one */
            if (fire_gfx_gpuHasExtension(exts, count, "VK_KHR_portability_enumeration")) {
                enabled[n++] = "VK_KHR_portability_enumeration";
                ci.flags = 0x00000001;   /* VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR */
            }
            /* the surfaces of the window systems, where they are there */
            if (fire_gfx_gpuHasExtension(exts, count, "VK_KHR_surface")) {
                enabled[n++] = "VK_KHR_surface";
                for (k = 0; k < 4; k++) if (fire_gfx_gpuHasExtension(exts, count, surfaces[k])) { enabled[n++] = surfaces[k]; surface = 1; }
            }
#ifdef _WIN32
            /* (DirectComposition: what the import of D3D11's textures needs of the instance) */
            if (fire_gfx_gpuHasExtension(exts, count, "VK_KHR_get_physical_device_properties2")) enabled[n++] = "VK_KHR_get_physical_device_properties2";
            if (fire_gfx_gpuHasExtension(exts, count, "VK_KHR_external_memory_capabilities")) enabled[n++] = "VK_KHR_external_memory_capabilities";
#endif
        }
        free(exts);
    }
    ci.enabledExtensionCount = n;
    ci.ppEnabledExtensionNames = n > 0 ? enabled : NULL;
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
    g_fireGfxVk.wsi = surface;
    if (surface) {
#define FIRE_GFX_VK_LOAD_WI(name) g_fireGfxVk.name = (PFN_##name)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, #name); if (g_fireGfxVk.name == NULL) g_fireGfxVk.wsi = 0;
        FIRE_GFX_VK_WSI_INSTANCE_FUNCTIONS(FIRE_GFX_VK_LOAD_WI)
#undef FIRE_GFX_VK_LOAD_WI
        g_fireGfxVk.createWin32Surface = (PFN_fireGfxCreateSurface)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, "vkCreateWin32SurfaceKHR");
        g_fireGfxVk.createXlibSurface = (PFN_fireGfxCreateSurface)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, "vkCreateXlibSurfaceKHR");
        g_fireGfxVk.createWaylandSurface = (PFN_fireGfxCreateSurface)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, "vkCreateWaylandSurfaceKHR");
        g_fireGfxVk.createMetalSurface = (PFN_fireGfxCreateSurface)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, "vkCreateMetalSurfaceEXT");
    }
#ifdef _WIN32
    g_fireGfxVk.vkGetPhysicalDeviceProperties2 = (PFN_vkGetPhysicalDeviceProperties2)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, "vkGetPhysicalDeviceProperties2KHR");
    if (g_fireGfxVk.vkGetPhysicalDeviceProperties2 == NULL)
        g_fireGfxVk.vkGetPhysicalDeviceProperties2 = (PFN_vkGetPhysicalDeviceProperties2)g_fireGfxVk.vkGetInstanceProcAddr(g_fireGfxVk.instance, "vkGetPhysicalDeviceProperties2");
#endif
    return 1;
}

/* A GPU (discrete before integrated before virtual) with a queue that draws; a CPU device only when asked for (FIRE_GFX_GPU=cpu). */
static fire_gfx_int fire_gfx_gpuPick(void) {
    uint32_t count = 0, i, best = 0, bestFamily = 0;
    fire_gfx_int bestScore = 0;
    VkPhysicalDevice* list;
    const char* env = getenv("FIRE_GFX_GPU");
    fire_gfx_int allowCpu = env != NULL && strcmp(env, "cpu") == 0;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkEnumeratePhysicalDevices(g_fireGfxVk.instance, &count, NULL), "vkEnumeratePhysicalDevices")) return 0;
    if (count == 0) { fire_gfx_gpuFail("Vulkan finds no GPU on this machine."); return 0; }
    list = (VkPhysicalDevice*)malloc(sizeof(VkPhysicalDevice) * count);
    if (list == NULL) { fire_gfx_gpuFail("Out of memory."); return 0; }
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkEnumeratePhysicalDevices(g_fireGfxVk.instance, &count, list), "vkEnumeratePhysicalDevices")) { free(list); return 0; }
    for (i = 0; i < count; i++) {
        VkPhysicalDeviceProperties props;
        VkQueueFamilyProperties* fams;
        uint32_t famCount = 0, k;
        fire_gfx_int score;
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

static fire_gfx_int fire_gfx_gpuDevice(void) {
    float priority = 1.0f;
    VkDeviceQueueCreateInfo qi;
    VkDeviceCreateInfo di;
    VkCommandPoolCreateInfo pi;
    VkCommandBufferAllocateInfo ai;
    VkFenceCreateInfo fi;
    VkExtensionProperties* exts;
    const char* enabled[10];
    uint32_t n = 0, count = 0;
    fire_gfx_int swapchain = 0, interop = 0;
    memset(&qi, 0, sizeof qi);
    qi.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qi.queueFamilyIndex = g_fireGfxVk.family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &priority;
    memset(&di, 0, sizeof di);
    di.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    di.queueCreateInfoCount = 1;
    di.pQueueCreateInfos = &qi;
    if (g_fireGfxVk.vkEnumerateDeviceExtensionProperties(g_fireGfxVk.phys, NULL, &count, NULL) == VK_SUCCESS && count > 0) {
        exts = (VkExtensionProperties*)malloc(sizeof(VkExtensionProperties) * count);
        if (exts && g_fireGfxVk.vkEnumerateDeviceExtensionProperties(g_fireGfxVk.phys, NULL, &count, exts) == VK_SUCCESS) {
            /* a portability implementation has to be told that the program knows it is one */
            if (fire_gfx_gpuHasExtension(exts, count, "VK_KHR_portability_subset")) enabled[n++] = "VK_KHR_portability_subset";
            if (g_fireGfxVk.wsi && fire_gfx_gpuHasExtension(exts, count, "VK_KHR_swapchain")) { enabled[n++] = "VK_KHR_swapchain"; swapchain = 1; }
#ifdef _WIN32
            /* DirectComposition: textures of D3D11 imported (the window's picture) */
            if (g_fireGfxVk.vkGetPhysicalDeviceProperties2 != NULL && fire_gfx_gpuHasExtension(exts, count, "VK_KHR_external_memory")
                && fire_gfx_gpuHasExtension(exts, count, "VK_KHR_external_memory_win32") && fire_gfx_gpuHasExtension(exts, count, "VK_KHR_dedicated_allocation")
                && fire_gfx_gpuHasExtension(exts, count, "VK_KHR_get_memory_requirements2")) {
                enabled[n++] = "VK_KHR_external_memory"; enabled[n++] = "VK_KHR_external_memory_win32";
                enabled[n++] = "VK_KHR_dedicated_allocation"; enabled[n++] = "VK_KHR_get_memory_requirements2";
                interop = 1;
            }
#endif
        }
        free(exts);
    }
    if (!swapchain) g_fireGfxVk.wsi = 0;
    di.enabledExtensionCount = n;
    di.ppEnabledExtensionNames = n > 0 ? enabled : NULL;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateDevice(g_fireGfxVk.phys, &di, NULL, &g_fireGfxVk.device), "vkCreateDevice")) return 0;
    if (interop) {
        /* the device's LUID (DirectComposition's D3D11 must be on the same adapter) and the import's memory types */
        VkPhysicalDeviceProperties2 p2;
        VkPhysicalDeviceIDProperties id;
        memset(&p2, 0, sizeof p2); memset(&id, 0, sizeof id);
        p2.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
        id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
        p2.pNext = &id;
        g_fireGfxVk.vkGetPhysicalDeviceProperties2(g_fireGfxVk.phys, &p2);
        memcpy(g_fireGfxVk.luid, id.deviceLUID, sizeof g_fireGfxVk.luid);
        g_fireGfxVk.luidValid = id.deviceLUIDValid != 0;
        g_fireGfxVk.getWin32HandleProps = (PFN_fireGfxWin32HandleProps)g_fireGfxVk.vkGetDeviceProcAddr(g_fireGfxVk.device, "vkGetMemoryWin32HandlePropertiesKHR");
        g_fireGfxVk.interop = g_fireGfxVk.luidValid && g_fireGfxVk.getWin32HandleProps != NULL;
    }
#define FIRE_GFX_VK_LOAD_D(name) \
    g_fireGfxVk.name = (PFN_##name)g_fireGfxVk.vkGetDeviceProcAddr(g_fireGfxVk.device, #name); \
    if (g_fireGfxVk.name == NULL) { fire_gfx_gpuFail("The Vulkan driver lacks " #name "."); return 0; }
    FIRE_GFX_VK_DEVICE_FUNCTIONS(FIRE_GFX_VK_LOAD_D)
#undef FIRE_GFX_VK_LOAD_D
    if (g_fireGfxVk.wsi) {
#define FIRE_GFX_VK_LOAD_WD(name) g_fireGfxVk.name = (PFN_##name)g_fireGfxVk.vkGetDeviceProcAddr(g_fireGfxVk.device, #name); if (g_fireGfxVk.name == NULL) g_fireGfxVk.wsi = 0;
        FIRE_GFX_VK_WSI_DEVICE_FUNCTIONS(FIRE_GFX_VK_LOAD_WD)
#undef FIRE_GFX_VK_LOAD_WD
    }
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

static fire_gfx_int fire_gfx_gpuMemoryType(uint32_t bits, VkMemoryPropertyFlags want) {
    uint32_t i;
    for (i = 0; i < g_fireGfxVk.memProps.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (g_fireGfxVk.memProps.memoryTypes[i].propertyFlags & want) == want) return (fire_gfx_int)i;
    return -1;
}

/* A buffer in host memory that the GPU reads and writes (mapped). */
static fire_gfx_int fire_gfx_gpuBuffer(FireGfxVkBuffer* b, VkDeviceSize size, VkBufferUsageFlags usage) {
    VkBufferCreateInfo bi;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    fire_gfx_int type;
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

static fire_gfx_int fire_gfx_gpuModule(const uint32_t* code, size_t size, VkShaderModule* out) {
    VkShaderModuleCreateInfo ci;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    ci.codeSize = size;
    ci.pCode = code;
    return fire_gfx_gpuCheck(g_fireGfxVk.vkCreateShaderModule(g_fireGfxVk.device, &ci, NULL, out), "vkCreateShaderModule");
}

/* The render pass (the picture stays: load and store, in the GENERAL layout throughout) and the two pipelines of the shapes: blending and copying. */
static fire_gfx_int fire_gfx_gpuPipelines(void) {
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
    fire_gfx_int k;
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
static fire_gfx_int fire_gfx_gpuImagePipelines(void) {
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
    fire_gfx_int k, type;
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

/* The pipelines of blur and materials (shaders/gfx_effect.*): no vertex input (the rectangle comes as a push constant), a texture; copying (into the temporary pictures)
 * and blending (into the framebuffer: alpha is the coverage). */
static fire_gfx_int fire_gfx_gpuEffectPipelines(void) {
    VkPushConstantRange push;
    VkPipelineLayoutCreateInfo li;
    VkPipelineShaderStageCreateInfo stages[2];
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
    fire_gfx_int k;
    if (!fire_gfx_gpuModule(fire_gfx_spv_effect_vert, sizeof fire_gfx_spv_effect_vert, &g_fireGfxVk.effectVert)) return 0;
    if (!fire_gfx_gpuModule(fire_gfx_spv_effect_frag, sizeof fire_gfx_spv_effect_frag, &g_fireGfxVk.effectFrag)) return 0;
    memset(&push, 0, sizeof push);
    push.stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    push.size = sizeof(FireGfxGpuPass);
    memset(&li, 0, sizeof li);
    li.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    li.setLayoutCount = 1;
    li.pSetLayouts = &g_fireGfxVk.setLayout;
    li.pushConstantRangeCount = 1;
    li.pPushConstantRanges = &push;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreatePipelineLayout(g_fireGfxVk.device, &li, NULL, &g_fireGfxVk.effectLayout), "vkCreatePipelineLayout")) return 0;
    memset(stages, 0, sizeof stages);
    for (k = 0; k < 2; k++) { stages[k].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO; stages[k].pName = "main"; }
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT; stages[0].module = g_fireGfxVk.effectVert;
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].module = g_fireGfxVk.effectFrag;
    memset(&vi, 0, sizeof vi);
    vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
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
    gi.layout = g_fireGfxVk.effectLayout;
    gi.renderPass = g_fireGfxVk.pass;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateGraphicsPipelines(g_fireGfxVk.device, NULL, 1, &gi, NULL, &g_fireGfxVk.effectCopy), "vkCreateGraphicsPipelines")) return 0;
    blendAtt.blendEnable = VK_TRUE;
    blendAtt.srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA;
    blendAtt.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blendAtt.colorBlendOp = VK_BLEND_OP_ADD;
    blendAtt.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    blendAtt.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    blendAtt.alphaBlendOp = VK_BLEND_OP_ADD;
    return fire_gfx_gpuCheck(g_fireGfxVk.vkCreateGraphicsPipelines(g_fireGfxVk.device, NULL, 1, &gi, NULL, &g_fireGfxVk.effectBlend), "vkCreateGraphicsPipelines");
}

static void fire_gfx_gpuFreeTemp(FireGfxGpuTemp* t) {
    if (t->set) g_fireGfxVk.vkFreeDescriptorSets(g_fireGfxVk.device, g_fireGfxVk.descPool, 1, &t->set);
    if (t->framebuffer) g_fireGfxVk.vkDestroyFramebuffer(g_fireGfxVk.device, t->framebuffer, NULL);
    if (t->view) g_fireGfxVk.vkDestroyImageView(g_fireGfxVk.device, t->view, NULL);
    if (t->image) g_fireGfxVk.vkDestroyImage(g_fireGfxVk.device, t->image, NULL);
    if (t->memory) g_fireGfxVk.vkFreeMemory(g_fireGfxVk.device, t->memory, NULL);
    memset(t, 0, sizeof *t);
}

/* A temporary picture of width x height (RGBA, drawn into and sampled). 0: it failed. */
static fire_gfx_int fire_gfx_gpuMakeTemp(FireGfxGpuTemp* t, fire_gfx_int width, fire_gfx_int height) {
    VkImageCreateInfo ii;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    VkImageViewCreateInfo vi;
    VkFramebufferCreateInfo fi;
    fire_gfx_int type;
    fire_gfx_gpuFreeTemp(t);
    memset(&ii, 0, sizeof ii);
    ii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = VK_FORMAT_R8G8B8A8_UNORM;
    ii.extent.width = (uint32_t)width; ii.extent.height = (uint32_t)height; ii.extent.depth = 1;
    ii.mipLevels = 1;
    ii.arrayLayers = 1;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_OPTIMAL;
    ii.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateImage(g_fireGfxVk.device, &ii, NULL, &t->image), "vkCreateImage")) return 0;
    g_fireGfxVk.vkGetImageMemoryRequirements(g_fireGfxVk.device, t->image, &req);
    type = fire_gfx_gpuMemoryType(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    if (type < 0) type = fire_gfx_gpuMemoryType(req.memoryTypeBits, 0);
    if (type < 0) return 0;
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = (uint32_t)type;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkAllocateMemory(g_fireGfxVk.device, &ai, NULL, &t->memory), "vkAllocateMemory")
        || !fire_gfx_gpuCheck(g_fireGfxVk.vkBindImageMemory(g_fireGfxVk.device, t->image, t->memory, 0), "vkBindImageMemory")) return 0;
    memset(&vi, 0, sizeof vi);
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = t->image;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = VK_FORMAT_R8G8B8A8_UNORM;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = 1;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateImageView(g_fireGfxVk.device, &vi, NULL, &t->view), "vkCreateImageView")) return 0;
    memset(&fi, 0, sizeof fi);
    fi.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
    fi.renderPass = g_fireGfxVk.pass;
    fi.attachmentCount = 1;
    fi.pAttachments = &t->view;
    fi.width = (uint32_t)width; fi.height = (uint32_t)height; fi.layers = 1;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkCreateFramebuffer(g_fireGfxVk.device, &fi, NULL, &t->framebuffer), "vkCreateFramebuffer")) return 0;
    t->set = fire_gfx_gpuSetFor(t->view);
    if (t->set == FIRE_GFX_NO_SET) return 0;
    t->width = width; t->height = height; t->ready = 0;
    return 1;
}

/* The temporary pictures hold at least width x height (they grow, they never shrink). */
static fire_gfx_int fire_gfx_gpuTemps(fire_gfx_int width, fire_gfx_int height) {
    fire_gfx_int k;
    for (k = 0; k < 2; k++) {
        FireGfxGpuTemp* t = &g_fireGfxVk.temps[k];
        if (t->width >= width && t->height >= height) continue;
        if (!fire_gfx_gpuMakeTemp(t, width > t->width ? width : t->width, height > t->height ? height : t->height)) return 0;
    }
    return 1;
}

/* Everything drawn before (into a picture, a transfer) is visible to what samples or draws after. */
static void fire_gfx_gpuPassBarrier(VkCommandBuffer cmd) {
    VkMemoryBarrier mb;
    memset(&mb, 0, sizeof mb);
    mb.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
    mb.srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_TRANSFER_WRITE_BIT;
    mb.dstAccessMask = VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    g_fireGfxVk.vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_TRANSFER_BIT,
                                     VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT | VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, 0, 1, &mb, 0, NULL, 0, NULL);
}

/* Begins the render pass into `framebuffer` (width x height), its viewport. */
static void fire_gfx_gpuBeginPass(VkCommandBuffer cmd, VkFramebuffer framebuffer, fire_gfx_int width, fire_gfx_int height) {
    VkRenderPassBeginInfo rb;
    VkViewport viewport;
    memset(&rb, 0, sizeof rb);
    rb.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
    rb.renderPass = g_fireGfxVk.pass;
    rb.framebuffer = framebuffer;
    rb.renderArea.extent.width = (uint32_t)width; rb.renderArea.extent.height = (uint32_t)height;
    g_fireGfxVk.vkCmdBeginRenderPass(cmd, &rb, VK_SUBPASS_CONTENTS_INLINE);
    memset(&viewport, 0, sizeof viewport);
    viewport.width = (float)width; viewport.height = (float)height; viewport.maxDepth = 1.0f;
    g_fireGfxVk.vkCmdSetViewport(cmd, 0, 1, &viewport);
}

/* The parameters of the effect as fire_gfx_fxBlurMaterial takes them: the factor of the smaller picture, the area around the rectangle, its size, the blur radius. */
static void fire_gfx_gpuEffectSizes(const FireGfxGpuTwin* t, const FireGfxGpuEffect* e, fire_gfx_int* f, fire_gfx_int* region, fire_gfx_int* tw, fire_gfx_int* th, fire_gfx_int* rb) {
    fire_gfx_int pad;
    *f = e->radius >= 10 ? 4 : e->radius >= 2 ? 2 : 1;
    pad = e->radius + *f;
    region[0] = e->x - pad < 0 ? 0 : e->x - pad; region[1] = e->y - pad < 0 ? 0 : e->y - pad;
    region[2] = e->x + e->w + pad > t->width ? t->width : e->x + e->w + pad; region[3] = e->y + e->h + pad > t->height ? t->height : e->y + e->h + pad;
    *tw = region[2] > region[0] ? (region[2] - region[0] + *f - 1) / *f : 0;
    *th = region[3] > region[1] ? (region[3] - region[1] + *f - 1) / *f : 0;
    *rb = (e->radius + *f) / (2 * *f);
}

static void fire_gfx_gpuBarrier(VkCommandBuffer cmd, VkImage image, VkImageLayout from);

/* One pass of an effect: `mode` into the temporary picture `to` (or, `to` NULL, into the framebuffer of the twin, blending) from the texture of `from`. */
static void fire_gfx_gpuEffectPass(VkCommandBuffer cmd, FireGfxGpuTwin* t, FireGfxGpuTemp* to, VkDescriptorSet from, FireGfxGpuPass* pc, const fire_gfx_int* scissorRect) {
    VkRect2D scissor;
    fire_gfx_int w = to ? to->width : t->width, h = to ? to->height : t->height;
    if (to != NULL && !to->ready) { fire_gfx_gpuBarrier(cmd, to->image, VK_IMAGE_LAYOUT_UNDEFINED); to->ready = 1; }
    fire_gfx_gpuBeginPass(cmd, to ? to->framebuffer : t->framebuffer, w, h);
    g_fireGfxVk.vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, to ? g_fireGfxVk.effectCopy : g_fireGfxVk.effectBlend);
    g_fireGfxVk.vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_fireGfxVk.effectLayout, 0, 1, &from, 0, NULL);
    pc->size[0] = (float)w; pc->size[1] = (float)h;
    g_fireGfxVk.vkCmdPushConstants(cmd, g_fireGfxVk.effectLayout, VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT, 0, sizeof *pc, pc);
    scissor.offset.x = scissorRect[0]; scissor.offset.y = scissorRect[1];
    scissor.extent.width = (uint32_t)scissorRect[2]; scissor.extent.height = (uint32_t)scissorRect[3];
    g_fireGfxVk.vkCmdSetScissor(cmd, 0, 1, &scissor);
    g_fireGfxVk.vkCmdDraw(cmd, 6, 1, 0, 0);
    g_fireGfxVk.vkCmdEndRenderPass(cmd);
    fire_gfx_gpuPassBarrier(cmd);
}

/* An effect (outside a render pass): smaller into temps[0], the box blur (along x twice, along y twice) back and forth, then into the framebuffer. */
static void fire_gfx_gpuRecordEffect(VkCommandBuffer cmd, FireGfxGpuTwin* t, const FireGfxGpuEffect* e) {
    FireGfxGpuPass pc;
    fire_gfx_int f, region[4], tw, th, rb, k, rect[4], x0, y0, x1, y1;
    FireGfxGpuTemp* a = &g_fireGfxVk.temps[0];
    FireGfxGpuTemp* b = &g_fireGfxVk.temps[1];
    fire_gfx_gpuEffectSizes(t, e, &f, region, &tw, &th, &rb);
    x0 = e->x > e->clip[0] ? e->x : e->clip[0]; y0 = e->y > e->clip[1] ? e->y : e->clip[1];
    x1 = e->x + e->w < e->clip[2] ? e->x + e->w : e->clip[2]; y1 = e->y + e->h < e->clip[3] ? e->y + e->h : e->clip[3];
    if (tw <= 0 || th <= 0 || x1 <= x0 || y1 <= y0) return;
    fire_gfx_gpuPassBarrier(cmd);
    memset(&pc, 0, sizeof pc);
    pc.f = f;
    memcpy(pc.region, region, sizeof region);
    pc.box[0] = e->x; pc.box[1] = e->y; pc.box[2] = e->w; pc.box[3] = e->h;
    pc.p[0] = e->corner; pc.p[1] = rb; pc.p[2] = e->kind; pc.p[3] = e->rim;
    pc.q[0] = (int32_t)e->tint; pc.q[1] = e->kind == FIRE_GFX_MATERIAL_GLASS ? 307 : e->kind == FIRE_GFX_MATERIAL_ACRYLIC ? 282 : 256; pc.q[2] = tw; pc.q[3] = th;
    rect[0] = 0; rect[1] = 0; rect[2] = tw; rect[3] = th;
    pc.mode = 0;
    memcpy(pc.rect, rect, sizeof rect);
    fire_gfx_gpuEffectPass(cmd, t, a, t->set, &pc, rect);
    if (rb > 0)
        for (k = 0; k < 4; k++) {
            pc.mode = k < 2 ? 1 : 2;
            fire_gfx_gpuEffectPass(cmd, t, (k & 1) ? a : b, (k & 1) ? b->set : a->set, &pc, rect);
        }
    pc.mode = 3;
    rect[0] = x0; rect[1] = y0; rect[2] = x1 - x0; rect[3] = y1 - y0;
    memcpy(pc.rect, rect, sizeof rect);
    fire_gfx_gpuEffectPass(cmd, t, NULL, a->set, &pc, rect);
}

/* Vulkan once (1: ready). */
static fire_gfx_int fire_gfx_gpuReady(void) {
    if (g_fireGfxVk.state != 0) return g_fireGfxVk.state > 0;
    g_fireGfxVk.state = -1;
    if (!fire_gfx_gpuLoad() || !fire_gfx_gpuInstance() || !fire_gfx_gpuPick() || !fire_gfx_gpuDevice() || !fire_gfx_gpuPipelines() || !fire_gfx_gpuImagePipelines() || !fire_gfx_gpuEffectPipelines()) return 0;
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
    free(t->effects);
    free(t->batches);
    free(t);
}

static FireGfxGpuTwin* fire_gfx_gpuMakeTwin(fire_gfx_int width, fire_gfx_int height) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)calloc(1, sizeof(FireGfxGpuTwin));
    VkImageCreateInfo ii;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    VkImageViewCreateInfo vi;
    VkFramebufferCreateInfo fi;
    fire_gfx_int type;
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
static fire_gfx_int fire_gfx_gpuFill(FireGfxVkBuffer* b, const void* data, size_t size, uint32_t count, VkBufferUsageFlags usage) {
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

static fire_gfx_int fire_gfx_gpuRunOne(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, fire_gfx_int fetch);

/* Runs the drawing of the twins that came into the list before `until` (all of them: NULL), the oldest first. One that blits from `until` came before its newer drawing
 * and must draw from the picture before it; one that came after may hold drawing newer than `until`'s (a source drawn over after the blit) and waits. (A blit runs
 * its source when it is queued: a twin never waits for a source that is still in the list.) */
static fire_gfx_int fire_gfx_gpuRunOthers(FireGfxGpuTwin* until) {
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
static fire_gfx_int fire_gfx_gpuRun(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, fire_gfx_int fetch) {
    if (!fire_gfx_gpuRunOthers(t)) return 0;
    return fire_gfx_gpuRunOne(fb, t, fetch);
}

static fire_gfx_int fire_gfx_gpuRunOneLocked(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, fire_gfx_int fetch);
static fire_gfx_int fire_gfx_gpuRunOne(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, fire_gfx_int fetch) {
    fire_gfx_int ok;
    fire_gfx_gpuLock();
    ok = fire_gfx_gpuRunOneLocked(fb, t, fetch);
    fire_gfx_gpuUnlock();
    return ok;
}
static fire_gfx_int fire_gfx_gpuRunOneLocked(FireGfxFramebuffer* fb, FireGfxGpuTwin* t, fire_gfx_int fetch) {
    VkCommandBuffer cmd = g_fireGfxVk.cmd;
    VkCommandBufferBeginInfo bi;
    VkSubmitInfo si;
    VkBufferImageCopy region;
    VkMemoryBarrier mb;
    size_t bytes = (size_t)t->width * (size_t)t->height * 4;
    fire_gfx_int upload = !t->gpuValid, draw = t->batchCount > 0, atlas = g_fireGfxVk.dirty[2] > g_fireGfxVk.dirty[0];
    fire_gfx_gpuUnlist(t);
    if (!upload && !draw && !(fetch && !t->cpuValid)) return 1;
    if (!fire_gfx_gpuFill(&g_fireGfxVk.shapes, t->shapes, sizeof(FireGfxGpuShape), t->shapeCount, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)
        || !fire_gfx_gpuFill(&g_fireGfxVk.images, t->images, sizeof(FireGfxGpuImage), t->imageCount, VK_BUFFER_USAGE_VERTEX_BUFFER_BIT)) return 0;
    {
        /* (the temporary pictures of the effects are big enough before anything is recorded: none is made anew while commands use it) */
        fire_gfx_int needW = 0, needH = 0, f, region[4], tw, th, rb;
        uint32_t k;
        for (k = 0; k < t->effectCount; k++) {
            fire_gfx_gpuEffectSizes(t, &t->effects[k], &f, region, &tw, &th, &rb);
            if (tw > needW) needW = tw;
            if (th > needH) needH = th;
        }
        if (needW > 0 && needH > 0 && !fire_gfx_gpuTemps(needW, needH)) return 0;
    }
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
        fire_gfx_int* d = g_fireGfxVk.dirty;
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
        VkDeviceSize offset = 0;
        float size[2];
        uint32_t k;
        fire_gfx_int inPass = 0;
        size[0] = (float)t->width; size[1] = (float)t->height;
        for (k = 0; k < t->batchCount; k++) {
            const FireGfxGpuBatch* b = &t->batches[k];
            VkRect2D scissor;
            if (b->kind == 2) {
                /* an effect: its passes sample what is drawn so far - the render pass of the framebuffer ends for them */
                uint32_t e;
                if (inPass) { g_fireGfxVk.vkCmdEndRenderPass(cmd); inPass = 0; }
                for (e = b->first; e < b->first + b->count; e++) fire_gfx_gpuRecordEffect(cmd, t, &t->effects[e]);
                continue;
            }
            if (!inPass) { fire_gfx_gpuPassBarrier(cmd); fire_gfx_gpuBeginPass(cmd, t->framebuffer, t->width, t->height); inPass = 1; }
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
        if (inPass) g_fireGfxVk.vkCmdEndRenderPass(cmd);
    }
    if (fetch && t->dirty[2] > t->dirty[0]) {
        /* back: only what the GPU drew since the pixels were the same (the rest of them is still right) */
        VkBufferImageCopy back = region;
        back.bufferOffset = ((VkDeviceSize)t->dirty[1] * (VkDeviceSize)t->width + (VkDeviceSize)t->dirty[0]) * 4;
        back.bufferRowLength = (uint32_t)t->width;
        back.imageOffset.x = t->dirty[0]; back.imageOffset.y = t->dirty[1];
        back.imageExtent.width = (uint32_t)(t->dirty[2] - t->dirty[0]); back.imageExtent.height = (uint32_t)(t->dirty[3] - t->dirty[1]);
        fire_gfx_gpuBarrier(cmd, t->image, VK_IMAGE_LAYOUT_GENERAL);
        g_fireGfxVk.vkCmdCopyImageToBuffer(cmd, t->image, VK_IMAGE_LAYOUT_GENERAL, t->staging.buffer, 1, &back);
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
    t->shapeCount = 0; t->imageCount = 0; t->effectCount = 0; t->batchCount = 0;
    if (draw) t->cpuValid = 0;
    if (fetch) {
        if (t->dirty[2] > t->dirty[0]) {
            fire_gfx_int y, x0 = t->dirty[0], n = (t->dirty[2] - t->dirty[0]) * 4;
            for (y = t->dirty[1]; y < t->dirty[3]; y++) {
                size_t at = ((size_t)y * (size_t)t->width + (size_t)x0) * 4;
                memcpy((uint8_t*)fb->pixels + at, (const uint8_t*)t->staging.mapped + at, (size_t)n);
            }
            g_fireGfxVk.readbacks++;
        }
        t->cpuValid = 1;
        t->dirty[0] = t->dirty[1] = t->dirty[2] = t->dirty[3] = 0;
    }
    return 1;
}

/* The CPU is about to read (`writes` 0) or draw (1) the framebuffer: it gets the current pixels first; after a drawing of the CPU the GPU's picture is old. */
static void fire_gfx_gpuCpu(FireGfxFramebuffer* fb, fire_gfx_int writes) {
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
static void fire_gfx_gpuSyncHook(FireGfxFramebuffer* fb, fire_gfx_int writes) { fire_gfx_gpuCpu(fb, writes); }
/* (a framebuffer goes: what others still draw from it is drawn first) */
static void fire_gfx_gpuForget(FireGfxFramebuffer* fb);
static void fire_gfx_gpuFreeHook(FireGfxFramebuffer* fb) { FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu; fb->gpu = NULL; fire_gfx_gpuForget(fb); fire_gfx_gpuUnlist(t); fire_gfx_gpuRunOthers(NULL); fire_gfx_gpuFreeTwin(t); }

static fire_gfx_int fire_gfx_gpuAttach(FireGfxFramebuffer* fb) {
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
    fire_gfx_gpuForget(fb);
    if (t != NULL) { fire_gfx_gpuUnlist(t); fire_gfx_gpuRunOthers(NULL); }
    fire_gfx_gpuFreeTwin(t);
}

/* The clip of the renderer within the picture. */
static fire_gfx_int fire_gfx_gpuClip(const FireGfxGpuTwin* t, fire_gfx_int* cl, fire_gfx_int* ct, fire_gfx_int* cr, fire_gfx_int* cb) {
    if (*cl < 0) *cl = 0;
    if (*ct < 0) *ct = 0;
    if (*cr > t->width) *cr = t->width;
    if (*cb > t->height) *cb = t->height;
    return *cr > *cl && *cb > *ct;
}

/* What the GPU draws (x0, y0)-(x1, y1) is no longer the same in the pixels of the CPU: it comes back with the next readback. */
static void fire_gfx_gpuDirty(FireGfxGpuTwin* t, fire_gfx_int x0, fire_gfx_int y0, fire_gfx_int x1, fire_gfx_int y1) {
    fire_gfx_int* d = t->dirty;
    if (x1 <= x0 || y1 <= y0) return;
    if (d[2] <= d[0]) { d[0] = x0; d[1] = y0; d[2] = x1; d[3] = y1; return; }
    if (x0 < d[0]) d[0] = x0;
    if (y0 < d[1]) d[1] = y0;
    if (x1 > d[2]) d[2] = x1;
    if (y1 > d[3]) d[3] = y1;
}

/* The batch the next command of `kind` goes into: the last one when it fits, else a new one. NULL without memory. The twin has drawing to do from now on. */
static FireGfxGpuBatch* fire_gfx_gpuBatch(FireGfxGpuTwin* t, fire_gfx_int kind, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int copy, VkDescriptorSet set) {
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
        b->first = kind == 0 ? t->shapeCount : kind == 1 ? t->imageCount : t->effectCount; b->count = 0;
    }
    fire_gfx_gpuList(t);
    t->cpuValid = 0;
    return b;
}

/* A rounded rectangle (x, y, w, h; radius; `stroke` > 0: its outline that wide) in `rgba`, clipped to (cl, ct)-(cr, cb), blended - or copied (`copy`: from half
 * coverage on, the colour with its alpha). 0: the GPU does not draw it (no twin): the caller draws it in software. */
static fire_gfx_int fire_gfx_gpuShape(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int radius, fire_gfx_int stroke, uint32_t rgba, fire_gfx_int copy) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    FireGfxGpuShape* s;
    FireGfxGpuBatch* b;
    fire_gfx_int x0, y0, x1, y1;
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
    fire_gfx_gpuDirty(t, x0, y0, x1, y1);
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
static fire_gfx_int fire_gfx_gpuPicture(FireGfxGpuTwin* t, VkDescriptorSet set, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int sx, fire_gfx_int sy, fire_gfx_int sw, fire_gfx_int sh, fire_gfx_int dx, fire_gfx_int dy, fire_gfx_int dw, fire_gfx_int dh,
                               fire_gfx_int mode, uint32_t rgba, fire_gfx_int copy) {
    FireGfxGpuImage* im;
    FireGfxGpuBatch* b;
    fire_gfx_int aw = dw < 0 ? -dw : dw, ah = dh < 0 ? -dh : dh, x0, y0, x1, y1;
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
    fire_gfx_gpuDirty(t, x0, y0, x1, y1);
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
static fire_gfx_int fire_gfx_gpuAtlasReset(void) {
    if (!fire_gfx_gpuRunOthers(NULL)) return 0;
    memset(g_fireGfxVk.slots, 0, sizeof(FireGfxAtlasSlot) * FIRE_GFX_ATLAS_SLOTS);
    g_fireGfxVk.slotCount = 0;
    g_fireGfxVk.shelfX = g_fireGfxVk.shelfY = g_fireGfxVk.shelfH = 0;
    return 1;
}

/* The place of glyph `key` (w x h) in the atlas: found, or made from `coverage` (w*h bytes; NULL: from the 8 bits of each row of `rows`). 0: it does not fit at all. */
static fire_gfx_int fire_gfx_gpuAtlasPlace(uint64_t key, fire_gfx_int w, fire_gfx_int h, fire_gfx_int maxH, const uint8_t* coverage, const uint8_t* rows, fire_gfx_int* ax, fire_gfx_int* ay) {
    uint64_t probe;
    FireGfxAtlasSlot* slot;
    fire_gfx_int tries, gy, gx;
    if (w <= 0 || h <= 0 || w > FIRE_GFX_ATLAS_SIZE || h > maxH) return 0;
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
/* (a glyph: at most 256 high) */
static fire_gfx_int fire_gfx_gpuAtlasGlyph(uint64_t key, fire_gfx_int w, fire_gfx_int h, const uint8_t* coverage, const uint8_t* rows, fire_gfx_int* ax, fire_gfx_int* ay) {
    return fire_gfx_gpuAtlasPlace(key, w, h, 256, coverage, rows, ax, ay);
}

/* A glyph at (x, y): its coverage times `rgba` (blended; `copy`: copied where it covers - as fire_gfx_put without blending). 0: the GPU cannot. */
static fire_gfx_int fire_gfx_gpuGlyph(FireGfxGpuTwin* t, const fire_gfx_int* clip, uint64_t key, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, const uint8_t* coverage, const uint8_t* rows, uint32_t rgba, fire_gfx_int copy) {
    fire_gfx_int ax, ay;
    if (!fire_gfx_gpuAtlasGlyph(key, w, h, coverage, rows, &ax, &ay)) return 0;
    return fire_gfx_gpuPicture(t, g_fireGfxVk.atlasSet, clip[0], clip[1], clip[2], clip[3], ax, ay, w, h, x, y, w, h, FIRE_GFX_GPU_GLYPH, rgba, copy);
}

/* ---- text ---- */

/* (what the glyphs of a TrueType text go to the GPU with) */
typedef struct FireGfxGpuText { FireGfxGpuTwin* t; const fire_gfx_int* clip; uint64_t font; fire_gfx_int x, base; uint32_t rgba; fire_gfx_int copy, ok; } FireGfxGpuText;
static void fire_gfx_gpuTtfGlyph(void* ctx, const FireGfxGlyph* g, fire_gfx_int penX) {
    FireGfxGpuText* d = (FireGfxGpuText*)ctx;
    if (!d->ok || !g->hasImage || g->m.minWidth <= 0 || g->m.minHeight <= 0) return;
    if (!fire_gfx_gpuGlyph(d->t, d->clip, fire_gfx_gpuHash(d->font ^ (uint64_t)g->key * 0x9E3779B97F4A7C15ULL), d->x + penX + g->m.xOffset, d->base + g->m.yOffset,
                           g->m.minWidth, g->m.minHeight, g->image, NULL, d->rgba, d->copy)) d->ok = 0;
}

/* Text on the GPU, as fire_gfx_drawTextIn and fire_gfx_rendererDrawText draw it: `font` NULL - the bitmap font of the renderer in its cells -, a built-in bitmap font or
 * TrueType at `size`; the colours already resolved (blended unless `copy`), `hasBg`: the cells / the line below in `bg`. 0: the GPU cannot (the caller draws it in software;
 * nothing was drawn when it says so before the first glyph - a glyph that does not fit in the atlas at all leaves the rest to the software as well). */
static fire_gfx_int fire_gfx_gpuText(FireGfxFramebuffer* fb, const fire_gfx_int* clipIn, fire_gfx_int copy, FireGfxTextFont* font, fire_gfx_int small, fire_gfx_int cellW, fire_gfx_int cellH, fire_gfx_int size, fire_gfx_int x, fire_gfx_int y,
                            const uint16_t* text, uint32_t n, uint32_t fg, fire_gfx_int hasBg, uint32_t bg) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    fire_gfx_int clip[4], fgVisible;
    uint32_t i;
    if (t == NULL || t->width != fb->width || t->height != fb->height) return 0;
    clip[0] = clipIn[0]; clip[1] = clipIn[1]; clip[2] = clipIn[2]; clip[3] = clipIn[3];
    if (hasBg && !copy && (bg >> 24) == 0) hasBg = 0;
    fgVisible = copy || (fg >> 24) != 0;
    if (!fgVisible && !hasBg) return 1;
    if (font == NULL || font->bitmap) {
        /* a bitmap font: per character its cell (the background) and its bits */
        fire_gfx_int sm = font == NULL ? small : font->smallBitmap;
        fire_gfx_int cw = font == NULL ? cellW : fire_gfx_fontCellWidth(font), ch = font == NULL ? cellH : fire_gfx_fontCellHeight(font);
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
static fire_gfx_int fire_gfx_gpuBlit(FireGfxFramebuffer* dst, const FireGfxFramebuffer* srcIn, fire_gfx_int sx, fire_gfx_int sy, fire_gfx_int sw, fire_gfx_int sh, fire_gfx_int dx, fire_gfx_int dy, fire_gfx_int dw, fire_gfx_int dh, fire_gfx_int mode, fire_gfx_int blend,
                            fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)dst->gpu;
    FireGfxFramebuffer* src = (FireGfxFramebuffer*)srcIn;
    FireGfxGpuTwin* st;
    fire_gfx_int gpuMode;
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

/* ---- shadows, blur and materials (as fire_gfx_fx.h) ---- */

/* fire_gfx_fxShadow on the GPU: the blurred mask (the software makes it once, fire_gfx_fxShadowMask) in the atlas, stretched and coloured by the shader. Blending only
 * (without it there is no shadow - 1). 0: the GPU cannot (no twin, a mask too big for the atlas): the caller draws it in software. */
static fire_gfx_int fire_gfx_gpuShadow(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int blend, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int radius, fire_gfx_int soft, uint32_t color, fire_gfx_int dy) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    FireGfxShadowMask* m;
    FireGfxGpuImage* im;
    FireGfxGpuBatch* b;
    fire_gfx_int ax, ay, ox, oy, x0, y0, x1, y1;
    uint64_t key;
    if (t == NULL || t->width != fb->width || t->height != fb->height) return 0;
    if (w <= 0 || h <= 0 || (color >> 24) == 0 || soft <= 0 || !blend) return 1;
    if (soft > 256) soft = 256;
    if (dy <= -32768 || dy >= 32768) return 0;
    m = fire_gfx_fxShadowMask(w, h, radius, soft);
    if (m == NULL) return 0;
    if (!fire_gfx_gpuClip(t, &cl, &ct, &cr, &cb)) return 1;
    ox = x - m->pad; oy = y - m->pad + dy;
    x0 = cl > ox ? cl : ox; x1 = cr < ox + m->mw * m->scale ? cr : ox + m->mw * m->scale;
    y0 = ct > oy ? ct : oy; y1 = cb < oy + m->mh * m->scale ? cb : oy + m->mh * m->scale;
    if (x1 <= x0 || y1 <= y0) return 1;
    /* (the mask by what makes it: a key of its own, apart from the glyphs) */
    key = fire_gfx_gpuHash(((uint64_t)3 << 60) ^ (uint64_t)(uint32_t)w);
    key = fire_gfx_gpuHash(key ^ (uint64_t)(uint32_t)h);
    key = fire_gfx_gpuHash(key ^ ((uint64_t)(uint32_t)radius << 32) ^ (uint64_t)(uint32_t)soft);
    if (!fire_gfx_gpuAtlasPlace(key, m->mw, m->mh, FIRE_GFX_ATLAS_SIZE / 2, m->mask, NULL, &ax, &ay)) return 0;
    if (t->imageCount == t->imageCap) {
        uint32_t cap = t->imageCap ? t->imageCap * 2 : 256;
        FireGfxGpuImage* grown = (FireGfxGpuImage*)realloc(t->images, sizeof(FireGfxGpuImage) * cap);
        if (grown == NULL) return 0;
        t->images = grown; t->imageCap = cap;
    }
    b = fire_gfx_gpuBatch(t, 1, cl, ct, cr, cb, 0, g_fireGfxVk.atlasSet);
    if (b == NULL) return 0;
    fire_gfx_gpuDirty(t, x0, y0, x1, y1);
    im = &t->images[t->imageCount++];
    im->quad[0] = (float)x0; im->quad[1] = (float)y0; im->quad[2] = (float)(x1 - x0); im->quad[3] = (float)(y1 - y0);
    im->dst[0] = x; im->dst[1] = y; im->dst[2] = w; im->dst[3] = h;
    im->src[0] = ax; im->src[1] = ay; im->src[2] = m->mw; im->src[3] = m->mh;
    im->mode[0] = 4; im->mode[1] = m->scale; im->mode[2] = radius; im->mode[3] = m->pad + dy * 65536;
    im->color = color;
    b->count++;
    return 1;
}

/* A blur or a material (fire_gfx_fxBlurMaterial) into the twin: its passes run in the order of the drawing. 0: no twin, or without memory. */
static fire_gfx_int fire_gfx_gpuEffect(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int corner, fire_gfx_int radius, fire_gfx_int kind, uint32_t tint, fire_gfx_int rim) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    FireGfxGpuEffect* e;
    FireGfxGpuBatch* b;
    if (t == NULL || t->width != fb->width || t->height != fb->height || t->set == FIRE_GFX_NO_SET) return 0;
    if (w <= 0 || h <= 0) return 1;
    if (!fire_gfx_gpuClip(t, &cl, &ct, &cr, &cb)) return 1;
    if (x >= cr || y >= cb || x + w <= cl || y + h <= ct) return 1;
    if (t->effectCount == t->effectCap) {
        uint32_t cap = t->effectCap ? t->effectCap * 2 : 16;
        FireGfxGpuEffect* grown = (FireGfxGpuEffect*)realloc(t->effects, sizeof(FireGfxGpuEffect) * cap);
        if (grown == NULL) return 0;
        t->effects = grown; t->effectCap = cap;
    }
    b = fire_gfx_gpuBatch(t, 2, cl, ct, cr, cb, 0, FIRE_GFX_NO_SET);
    if (b == NULL) return 0;
    fire_gfx_gpuDirty(t, x > cl ? x : cl, y > ct ? y : ct, x + w < cr ? x + w : cr, y + h < cb ? y + h : cb);
    e = &t->effects[t->effectCount++];
    e->clip[0] = cl; e->clip[1] = ct; e->clip[2] = cr; e->clip[3] = cb;
    e->x = x; e->y = y; e->w = w; e->h = h; e->corner = corner; e->radius = radius; e->kind = kind; e->rim = rim;
    e->tint = kind == FIRE_GFX_MATERIAL_TINT ? 0 : tint;
    b->count++;
    return 1;
}

/* fire_gfx_fxBlur on the GPU. 0: the caller blurs in software. */
static fire_gfx_int fire_gfx_gpuBlur(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int corner, fire_gfx_int radius) {
    if (fb->gpu == NULL) return 0;
    if (radius <= 0) return 1;
    if (radius > 64) radius = 64;
    return fire_gfx_gpuEffect(fb, cl, ct, cr, cb, x, y, w, h, corner, radius, FIRE_GFX_MATERIAL_TINT, 0, 0);
}

/* fire_gfx_fxMaterial on the GPU (an RGBA framebuffer with a twin): the same branches - opaque, a translucent tint, the blurred material - and the hairline. 0: the caller
 * draws it in software (nothing was drawn then). */
static fire_gfx_int fire_gfx_gpuMaterial(FireGfxFramebuffer* fb, fire_gfx_int cl, fire_gfx_int ct, fire_gfx_int cr, fire_gfx_int cb, fire_gfx_int blend, fire_gfx_int x, fire_gfx_int y, fire_gfx_int w, fire_gfx_int h, fire_gfx_int corner, fire_gfx_int kind, uint32_t tint, uint32_t base, fire_gfx_int flags) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    fire_gfx_int dark = (flags & FIRE_GFX_FX_DARK) != 0;
    if (t == NULL || t->width != fb->width || t->height != fb->height || t->set == FIRE_GFX_NO_SET) return 0;
    if (w <= 0 || h <= 0) return 1;
    if (!(flags & FIRE_GFX_FX_TRANSPARENCY)) {
        uint32_t c = fire_gfx_mix(base | 0xFF000000u, tint);
        if ((blend && (c >> 24) != 0) || !blend) { if (!fire_gfx_gpuShape(fb, cl, ct, cr, cb, x, y, w, h, corner, 0, c, !blend)) return 0; }
    } else if (kind == FIRE_GFX_MATERIAL_TINT || !(flags & FIRE_GFX_FX_BLUR) || !blend) {
        uint32_t a = tint >> 24, c;
        if (kind == FIRE_GFX_MATERIAL_GLASS) a = a + (255 - a) / 3;
        else if (kind == FIRE_GFX_MATERIAL_ACRYLIC) a = a + (255 - a) / 2;
        c = (tint & 0x00FFFFFFu) | (a << 24);
        if (!blend || a != 0) { if (!fire_gfx_gpuShape(fb, cl, ct, cr, cb, x, y, w, h, corner, 0, c, !blend)) return 0; }
    } else {
        if (!fire_gfx_gpuEffect(fb, cl, ct, cr, cb, x, y, w, h, corner, kind == FIRE_GFX_MATERIAL_ACRYLIC ? 10 : 7, kind, tint, dark ? 10 : 18)) return 0;
    }
    /* the hairline: 1 px inside the edge, a little lighter at the top */
    {
        uint32_t edge = dark ? 0x1CFFFFFFu : 0x1A000000u, top = dark ? 0x22FFFFFFu : 0x40FFFFFFu;
        if (!fire_gfx_gpuShape(fb, cl, ct, cr, cb, x, y, w, h, corner, 1, edge, !blend)) return 0;
        if (h > 2 * corner + 2 && w - 2 * corner - 2 > 0 && !fire_gfx_gpuShape(fb, cl, ct, cr, cb, x + corner + 1, y + 1, w - 2 * corner - 2, 1, 0, 0, top, !blend)) return 0;
    }
    return 1;
}

/* ---- the picture straight into a window (part 4.4) ----
 * A window that shows an accelerated framebuffer gets its picture from the GPU: when the program publishes a frame (Window.Present, Tick), the twin's picture is copied
 * into the picture of the framebuffer's presenter (on the GPU - nothing comes back), and the window's thread draws that into the window through a swap chain
 * (VK_KHR_swapchain on a surface for Win32, X11, Wayland or a CAMetalLayer) - the presenter of the windows bridge (fire_present_fn), as scene3d's DirectDrawing. The first
 * frames are read back as well (the window may not take a presenter: then nothing changes); once a window showed one, the frames stay on the GPU. A presenter that fails
 * gives the window back for good, and the frames are read back again. */

typedef struct FireGfxGpuTarget {
    FireGfxFramebuffer* fb;           /* whose frames (NULL: the framebuffer is gone or no longer accelerated) */
    volatile fire_gfx_int works;               /* 0: not known yet (the frames are read back as well), 1: a window shows them, -1: it failed */
    volatile fire_gfx_int others;              /* another window shows the framebuffer too: it needs the pixels */
    FireGfxGpuTemp pic; fire_gfx_int hasPicture;   /* the picture of the last frame (premultiplied when `transparent`) */
    volatile fire_gfx_int transparent;         /* the window shows what lies behind it (its backdrop): the frame goes premultiplied into a swap chain that mixes */
    fire_gfx_int alpha;                        /* the swap chain was made for that */
    volatile fire_gfx_int noAlpha;             /* the window's surface cannot mix by alpha: such frames go as pixels */
    fire_native_window win;           /* the window of the surface */
    VkSurfaceKHR surface; VkSwapchainKHR swapchain; VkExtent2D extent; fire_gfx_int vsync, stale;
    VkImage* images; VkSemaphore* rendered; uint32_t imageCount;
    VkSemaphore acquired;
    VkCommandPool pool; VkCommandBuffer cmd; VkFence fence; fire_gfx_int fencePending;
    /* DirectComposition (FIRE_NATIVE_DCOMP): the window's picture, imported */
    fire_gfx_int dcomp;                        /* the window of `win` is one of DirectComposition */
    void* dcHandle; fire_gfx_int dcW, dcH, dcReady;
    VkImage dcImage; VkDeviceMemory dcMemory;
    struct FireGfxGpuTarget* next;
} FireGfxGpuTarget;

static fire_gfx_int fire_gfx_gpuHolds(const FireGfxGpuTarget* g);
static uint32_t fire_gfx_gpuReadbacks(void) { return g_fireGfxVk.readbacks; }

static FireGfxGpuTarget* fire_gfx_gpuTargetOf(FireGfxFramebuffer* fb, fire_gfx_int make) {
    FireGfxGpuTarget* g;
    for (g = g_fireGfxVk.targets; g != NULL; g = g->next) if (g->fb == fb) return g;
    if (!make) return NULL;
    /* (one that lost its framebuffer and its window is used again) */
    for (g = g_fireGfxVk.targets; g != NULL; g = g->next) if (g->fb == NULL && !fire_gfx_gpuHolds(g)) { g->fb = fb; g->works = 0; g->others = 0; g->noAlpha = 0; g->hasPicture = 0; return g; }
    g = (FireGfxGpuTarget*)calloc(1, sizeof(FireGfxGpuTarget));
    if (g == NULL) return NULL;
    g->fb = fb;
    g->next = g_fireGfxVk.targets;
    g_fireGfxVk.targets = g;
    return g;
}

/* (the framebuffer is gone or no longer accelerated: its presenter shows nothing new) */
static void fire_gfx_gpuForget(FireGfxFramebuffer* fb) {
    FireGfxGpuTarget* g;
    fire_gfx_gpuLock();
    for (g = g_fireGfxVk.targets; g != NULL; g = g->next) if (g->fb == fb) g->fb = NULL;
    fire_gfx_gpuUnlock();
}

static void fire_gfx_gpuWaitTarget(FireGfxGpuTarget* g) {
    if (!g->fencePending) return;
    g_fireGfxVk.vkWaitForFences(g_fireGfxVk.device, 1, &g->fence, VK_TRUE, ~(uint64_t)0);
    g->fencePending = 0;
}

static void fire_gfx_gpuFreeTargetPicture(FireGfxGpuTarget* g) {
    fire_gfx_gpuWaitTarget(g);
    fire_gfx_gpuFreeTemp(&g->pic);
    g->hasPicture = 0;
}

/* The picture of the presenter, w x h (made anew for another size). */
static fire_gfx_int fire_gfx_gpuTargetPicture(FireGfxGpuTarget* g, fire_gfx_int width, fire_gfx_int height) {
    if (g->pic.image && g->pic.width == width && g->pic.height == height) return 1;
    fire_gfx_gpuFreeTargetPicture(g);
    if (!fire_gfx_gpuMakeTemp(&g->pic, width, height)) { fire_gfx_gpuFreeTemp(&g->pic); return 0; }
    return 1;
}

/* The twin's picture into the presenter's (the lock is held). */
static fire_gfx_int fire_gfx_gpuSnapshotLocked(FireGfxGpuTarget* g, FireGfxGpuTwin* t) {
    VkCommandBuffer cmd = g_fireGfxVk.cmd;
    VkCommandBufferBeginInfo bi;
    VkSubmitInfo si;
    VkImageCopy region;
    fire_gfx_gpuWaitTarget(g);   /* (the window's last picture read it) */
    if (!fire_gfx_gpuTargetPicture(g, t->width, t->height)) return 0;
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    g_fireGfxVk.vkResetCommandBuffer(cmd, 0);
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkBeginCommandBuffer(cmd, &bi), "vkBeginCommandBuffer")) return 0;
    fire_gfx_gpuBarrier(cmd, t->image, VK_IMAGE_LAYOUT_GENERAL);
    if (g->transparent) {
        /* premultiplied (a pass of the effect shader: mode 4) */
        FireGfxGpuPass pc;
        fire_gfx_int rect[4];
        memset(&pc, 0, sizeof pc);
        pc.mode = 4;
        rect[0] = 0; rect[1] = 0; rect[2] = t->width; rect[3] = t->height;
        memcpy(pc.rect, rect, sizeof rect);
        fire_gfx_gpuEffectPass(cmd, t, &g->pic, t->set, &pc, rect);
    } else {
        if (!g->pic.ready) { fire_gfx_gpuBarrier(cmd, g->pic.image, VK_IMAGE_LAYOUT_UNDEFINED); g->pic.ready = 1; }
        else fire_gfx_gpuBarrier(cmd, g->pic.image, VK_IMAGE_LAYOUT_GENERAL);
        memset(&region, 0, sizeof region);
        region.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; region.srcSubresource.layerCount = 1;
        region.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; region.dstSubresource.layerCount = 1;
        region.extent.width = (uint32_t)t->width; region.extent.height = (uint32_t)t->height; region.extent.depth = 1;
        g_fireGfxVk.vkCmdCopyImage(cmd, t->image, VK_IMAGE_LAYOUT_GENERAL, g->pic.image, VK_IMAGE_LAYOUT_GENERAL, 1, &region);
    }
    fire_gfx_gpuBarrier(cmd, g->pic.image, VK_IMAGE_LAYOUT_GENERAL);
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkEndCommandBuffer(cmd), "vkEndCommandBuffer")) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    g_fireGfxVk.vkResetFences(g_fireGfxVk.device, 1, &g_fireGfxVk.fence);
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkQueueSubmit(g_fireGfxVk.queue, 1, &si, g_fireGfxVk.fence), "vkQueueSubmit")) return 0;
    if (!fire_gfx_gpuCheck(g_fireGfxVk.vkWaitForFences(g_fireGfxVk.device, 1, &g_fireGfxVk.fence, VK_TRUE, ~(uint64_t)0), "vkWaitForFences")) return 0;
    g->hasPicture = 1;
    return 1;
}

static int32_t fire_gfx_gpuPresent(void* ctx, int32_t op, const fire_native_window* w, const uint32_t* pixels, int32_t width, int32_t height);

/* A frame of the framebuffer is published (on the program's thread): with a twin and windows the GPU can draw into, its picture goes to the presenter, and `fn`/`ctx` are
 * the presenter for the windows. Returns 1 when the frame needs the pixels as well (no presenter, or none of the windows showed one yet), 0 when it does not. */
static fire_gfx_int fire_gfx_gpuPublish(FireGfxFramebuffer* fb, fire_gfx_int transparent, void (**fn)(void), void** ctx) {
    FireGfxGpuTwin* t = (FireGfxGpuTwin*)fb->gpu;
    FireGfxGpuTarget* g;
    fire_gfx_int ok;
    *fn = NULL; *ctx = NULL;
    if (t == NULL || g_fireGfxVk.state != 1 || !(g_fireGfxVk.wsi || g_fireGfxVk.interop) || t->width != fb->width || t->height != fb->height || fb->pixels == NULL) return 1;
    fire_gfx_gpuLock();
    g = fire_gfx_gpuTargetOf(fb, 1);
    fire_gfx_gpuUnlock();
    if (g == NULL || g->works < 0 || (transparent && g->noAlpha)) return 1;
    /* the frame as it is: the CPU's drawing goes up, the GPU's runs */
    if ((!t->gpuValid || t->batchCount > 0) && !fire_gfx_gpuRun(fb, t, 0)) return 1;
    fire_gfx_gpuLock();
    g->transparent = transparent != 0;
    ok = fire_gfx_gpuSnapshotLocked(g, t);
    fire_gfx_gpuUnlock();
    if (!ok) return 1;
    *fn = (void (*)(void))fire_gfx_gpuPresent; *ctx = g;
    return g->works != 1 || g->others;
}

/* ---- on the window's thread ---- */

static fire_gfx_int fire_gfx_gpuSameWindow(const fire_native_window* a, const fire_native_window* b) { return a->kind == b->kind && a->a == b->a && a->b == b->b && a->id == b->id; }

static void fire_gfx_gpuFreeSwapchainImages(FireGfxGpuTarget* g) {
    uint32_t i;
    for (i = 0; i < g->imageCount; i++) if (g->rendered && g->rendered[i]) g_fireGfxVk.vkDestroySemaphore(g_fireGfxVk.device, g->rendered[i], NULL);
    free(g->images); free(g->rendered);
    g->images = NULL; g->rendered = NULL; g->imageCount = 0;
}

/* Lets go of the window: the swap chain and the surface. */
static void fire_gfx_gpuFreeImport(FireGfxGpuTarget* g) {
    fire_gfx_gpuWaitTarget(g);
    if (g->dcImage) g_fireGfxVk.vkDestroyImage(g_fireGfxVk.device, g->dcImage, NULL);
    if (g->dcMemory) g_fireGfxVk.vkFreeMemory(g_fireGfxVk.device, g->dcMemory, NULL);
    g->dcImage = 0; g->dcMemory = 0; g->dcHandle = NULL; g->dcW = g->dcH = 0; g->dcReady = 0;
}

/* (the target holds a window: a surface, or one of DirectComposition) */
static fire_gfx_int fire_gfx_gpuHolds(const FireGfxGpuTarget* g) { return g->surface != 0 || g->dcomp; }

static void fire_gfx_gpuFreeSurface(FireGfxGpuTarget* g) {
    if (g->dcomp) { fire_gfx_gpuFreeImport(g); g->dcomp = 0; memset(&g->win, 0, sizeof g->win); if (g->fb == NULL) fire_gfx_gpuFreeTargetPicture(g); }
    if (!g->surface && !g->swapchain) return;
    fire_gfx_gpuWaitTarget(g);
    g_fireGfxVk.vkDeviceWaitIdle(g_fireGfxVk.device);
    fire_gfx_gpuFreeSwapchainImages(g);
    if (g->swapchain) { g_fireGfxVk.vkDestroySwapchainKHR(g_fireGfxVk.device, g->swapchain, NULL); g->swapchain = 0; }
    if (g->surface) { g_fireGfxVk.vkDestroySurfaceKHR(g_fireGfxVk.instance, g->surface, NULL); g->surface = 0; }
    memset(&g->win, 0, sizeof g->win);
    if (g->fb == NULL) fire_gfx_gpuFreeTargetPicture(g);   /* (nobody publishes into it any more) */
}

static fire_gfx_int fire_gfx_gpuMakeSurface(FireGfxGpuTarget* g, const fire_native_window* w) {
    VkResult r = VK_ERROR_EXTENSION_NOT_PRESENT;
    VkBool32 supported = VK_FALSE;
    switch (w->kind) {
        case FIRE_NATIVE_WIN32: if (g_fireGfxVk.createWin32Surface) {
            FireGfxVkWin32SurfaceInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = (VkStructureType)1000009000; ci.hinstance = w->a; ci.hwnd = w->b;   /* VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR */
            r = g_fireGfxVk.createWin32Surface(g_fireGfxVk.instance, &ci, NULL, &g->surface);
        } break;
        case FIRE_NATIVE_X11: if (g_fireGfxVk.createXlibSurface) {
            FireGfxVkXlibSurfaceInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = (VkStructureType)1000004000; ci.dpy = w->a; ci.window = (unsigned long)w->id;   /* VK_STRUCTURE_TYPE_XLIB_SURFACE_CREATE_INFO_KHR */
            r = g_fireGfxVk.createXlibSurface(g_fireGfxVk.instance, &ci, NULL, &g->surface);
        } break;
        case FIRE_NATIVE_WAYLAND: if (g_fireGfxVk.createWaylandSurface) {
            FireGfxVkWaylandSurfaceInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = (VkStructureType)1000006000; ci.display = w->a; ci.surface = w->b;   /* VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR */
            r = g_fireGfxVk.createWaylandSurface(g_fireGfxVk.instance, &ci, NULL, &g->surface);
        } break;
        case FIRE_NATIVE_METAL: if (g_fireGfxVk.createMetalSurface) {
            FireGfxVkMetalSurfaceInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = (VkStructureType)1000217000; ci.pLayer = w->b;   /* VK_STRUCTURE_TYPE_METAL_SURFACE_CREATE_INFO_EXT */
            r = g_fireGfxVk.createMetalSurface(g_fireGfxVk.instance, &ci, NULL, &g->surface);
        } break;
        default: break;
    }
    if (r != VK_SUCCESS) { g->surface = 0; return 0; }
    if (g_fireGfxVk.vkGetPhysicalDeviceSurfaceSupportKHR(g_fireGfxVk.phys, g_fireGfxVk.family, g->surface, &supported) != VK_SUCCESS || !supported) {
        g_fireGfxVk.vkDestroySurfaceKHR(g_fireGfxVk.instance, g->surface, NULL);
        g->surface = 0;
        return 0;
    }
    g->win = *w;
    return 1;
}

/* The swap chain, as big as the window (made again when it changes): 1 ready, 2 the window has no size (minimized), 0 it cannot. The frame goes in by a blit: the
 * images must take one (TRANSFER_DST, a UNORM format that can be blitted to - the bytes as they are, not sRGB). */
static fire_gfx_int fire_gfx_gpuMakeSwapchain(FireGfxGpuTarget* g, const fire_native_window* w) {
    VkSurfaceCapabilitiesKHR caps;
    VkSurfaceFormatKHR* formats = NULL;
    VkPresentModeKHR modes[16];
    VkFormatProperties fp;
    uint32_t nf = 0, nm = 16, i, count = 0;
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkColorSpaceKHR space = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    VkPresentModeKHR mode = VK_PRESENT_MODE_FIFO_KHR;
    VkExtent2D extent;
    VkSwapchainCreateInfoKHR ci;
    VkSwapchainKHR old = g->swapchain;
    VkSemaphoreCreateInfo sci;
    if (g_fireGfxVk.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(g_fireGfxVk.phys, g->surface, &caps) != VK_SUCCESS) return 0;
    extent = caps.currentExtent;
    if (extent.width == 0xFFFFFFFFu) {
        /* (Wayland: the window takes the size of the swap chain) */
        extent.width = (uint32_t)(w->width > 0 ? w->width : 1); extent.height = (uint32_t)(w->height > 0 ? w->height : 1);
        if (extent.width < caps.minImageExtent.width) extent.width = caps.minImageExtent.width;
        if (extent.height < caps.minImageExtent.height) extent.height = caps.minImageExtent.height;
        if (caps.maxImageExtent.width > 0 && extent.width > caps.maxImageExtent.width) extent.width = caps.maxImageExtent.width;
        if (caps.maxImageExtent.height > 0 && extent.height > caps.maxImageExtent.height) extent.height = caps.maxImageExtent.height;
    }
    if (extent.width == 0 || extent.height == 0) return 2;
    if (g->swapchain && !g->stale && extent.width == g->extent.width && extent.height == g->extent.height && (w->vsync != 0) == (g->vsync != 0) && g->alpha == g->transparent) return 1;
    if (!(caps.supportedUsageFlags & VK_IMAGE_USAGE_TRANSFER_DST_BIT)) return 0;
    /* (a window that shows what lies behind it: the swap chain mixes by the premultiplied alpha - where the surface cannot, those frames go as pixels) */
    if (g->transparent && !(caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR)
        && !(w->kind == FIRE_NATIVE_X11 && (caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR))) { g->noAlpha = 1; return 0; }
    if (g_fireGfxVk.vkGetPhysicalDeviceSurfaceFormatsKHR(g_fireGfxVk.phys, g->surface, &nf, NULL) != VK_SUCCESS || nf == 0) return 0;
    formats = (VkSurfaceFormatKHR*)malloc(sizeof(VkSurfaceFormatKHR) * nf);
    if (formats == NULL || g_fireGfxVk.vkGetPhysicalDeviceSurfaceFormatsKHR(g_fireGfxVk.phys, g->surface, &nf, formats) != VK_SUCCESS) { free(formats); return 0; }
    if (nf == 1 && formats[0].format == VK_FORMAT_UNDEFINED) { format = VK_FORMAT_B8G8R8A8_UNORM; space = formats[0].colorSpace; }
    for (i = 0; i < nf && format == VK_FORMAT_UNDEFINED; i++)
        if (formats[i].format == VK_FORMAT_B8G8R8A8_UNORM || formats[i].format == VK_FORMAT_R8G8B8A8_UNORM) { format = formats[i].format; space = formats[i].colorSpace; }
    free(formats);
    if (format == VK_FORMAT_UNDEFINED) return 0;
    g_fireGfxVk.vkGetPhysicalDeviceFormatProperties(g_fireGfxVk.phys, format, &fp);
    if (!(fp.optimalTilingFeatures & VK_FORMAT_FEATURE_BLIT_DST_BIT)) return 0;
    /* VSync: FIFO (every driver has it); without: MAILBOX (no tearing) or IMMEDIATE */
    if (!w->vsync && g_fireGfxVk.vkGetPhysicalDeviceSurfacePresentModesKHR(g_fireGfxVk.phys, g->surface, &nm, modes) >= 0) {
        for (i = 0; i < nm; i++) if (modes[i] == VK_PRESENT_MODE_MAILBOX_KHR) mode = VK_PRESENT_MODE_MAILBOX_KHR;
        if (mode != VK_PRESENT_MODE_MAILBOX_KHR) for (i = 0; i < nm; i++) if (modes[i] == VK_PRESENT_MODE_IMMEDIATE_KHR) mode = VK_PRESENT_MODE_IMMEDIATE_KHR;
    }
    fire_gfx_gpuWaitTarget(g);
    fire_gfx_gpuFreeSwapchainImages(g);
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    ci.surface = g->surface;
    ci.minImageCount = caps.minImageCount + 1;
    if (caps.maxImageCount > 0 && ci.minImageCount > caps.maxImageCount) ci.minImageCount = caps.maxImageCount;
    ci.imageFormat = format;
    ci.imageColorSpace = space;
    ci.imageExtent = extent;
    ci.imageArrayLayers = 1;
    ci.imageUsage = VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.preTransform = caps.currentTransform;
    /* (X11: INHERIT puts the premultiplied pixels into the window's 32-bit visual as they are - the X compositor mixes them, as it does those of the display) */
    ci.compositeAlpha = g->transparent ? ((caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR) ? VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR : VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR)
                      : (caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR) ? VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR
                      : (caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR) ? VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR : VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR;
    ci.presentMode = mode;
    ci.clipped = VK_TRUE;
    ci.oldSwapchain = old;
    if (g_fireGfxVk.vkCreateSwapchainKHR(g_fireGfxVk.device, &ci, NULL, &g->swapchain) != VK_SUCCESS) {
        g->swapchain = 0;
        if (old) g_fireGfxVk.vkDestroySwapchainKHR(g_fireGfxVk.device, old, NULL);
        return 0;
    }
    if (old) g_fireGfxVk.vkDestroySwapchainKHR(g_fireGfxVk.device, old, NULL);
    g->extent = extent; g->vsync = w->vsync != 0; g->stale = 0; g->alpha = g->transparent;
    if (g_fireGfxVk.vkGetSwapchainImagesKHR(g_fireGfxVk.device, g->swapchain, &count, NULL) != VK_SUCCESS || count == 0) return 0;
    g->images = (VkImage*)calloc(count, sizeof(VkImage));
    g->rendered = (VkSemaphore*)calloc(count, sizeof(VkSemaphore));
    if (!g->images || !g->rendered) return 0;
    g->imageCount = count;
    if (g_fireGfxVk.vkGetSwapchainImagesKHR(g_fireGfxVk.device, g->swapchain, &count, g->images) != VK_SUCCESS) return 0;
    memset(&sci, 0, sizeof sci);
    sci.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    for (i = 0; i < count; i++) if (g_fireGfxVk.vkCreateSemaphore(g_fireGfxVk.device, &sci, NULL, &g->rendered[i]) != VK_SUCCESS) { g->rendered[i] = 0; return 0; }
    return 1;
}

/* What the presenter needs of its own: commands (a pool of its own - the program's thread records into the other one meanwhile), a fence, the semaphore of the acquire. */
static fire_gfx_int fire_gfx_gpuTargetCommands(FireGfxGpuTarget* g) {
    VkCommandPoolCreateInfo pi;
    VkCommandBufferAllocateInfo ai;
    VkFenceCreateInfo fi;
    VkSemaphoreCreateInfo si;
    if (g->pool) return 1;
    memset(&pi, 0, sizeof pi);
    pi.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pi.queueFamilyIndex = g_fireGfxVk.family;
    if (g_fireGfxVk.vkCreateCommandPool(g_fireGfxVk.device, &pi, NULL, &g->pool) != VK_SUCCESS) { g->pool = 0; return 0; }
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    ai.commandPool = g->pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = 1;
    memset(&fi, 0, sizeof fi);
    fi.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    if (g_fireGfxVk.vkAllocateCommandBuffers(g_fireGfxVk.device, &ai, &g->cmd) != VK_SUCCESS
        || g_fireGfxVk.vkCreateFence(g_fireGfxVk.device, &fi, NULL, &g->fence) != VK_SUCCESS
        || g_fireGfxVk.vkCreateSemaphore(g_fireGfxVk.device, &si, NULL, &g->acquired) != VK_SUCCESS) {
        /* (what was made stays: the presenter is not used again) */
        return 0;
    }
    return 1;
}

static void fire_gfx_gpuImageBarrier(VkCommandBuffer cmd, VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags srcAccess, VkAccessFlags dstAccess,
                                     VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage) {
    VkImageMemoryBarrier b;
    memset(&b, 0, sizeof b);
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = srcAccess; b.dstAccessMask = dstAccess;
    b.oldLayout = from; b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = image;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1; b.subresourceRange.layerCount = 1;
    g_fireGfxVk.vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, NULL, 0, NULL, 1, &b);
}

/* The last frame into the window (the lock is held). 1: shown (or nothing to show: a minimized window), 0: it cannot. */
/* A barrier on an image that DirectComposition shares: the queue family `from` gives it to `to` (VK_QUEUE_FAMILY_EXTERNAL: D3D11). */
static void fire_gfx_gpuShareBarrier(VkCommandBuffer cmd, VkImage image, VkImageLayout from, uint32_t fromFamily, uint32_t toFamily, VkAccessFlags srcAccess, VkAccessFlags dstAccess) {
    VkImageMemoryBarrier b;
    memset(&b, 0, sizeof b);
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = srcAccess; b.dstAccessMask = dstAccess;
    b.oldLayout = from; b.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.srcQueueFamilyIndex = fromFamily; b.dstQueueFamilyIndex = toFamily;
    b.image = image;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1; b.subresourceRange.layerCount = 1;
    g_fireGfxVk.vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT | VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT | VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                                     0, 0, NULL, 0, NULL, 1, &b);
}

/* The D3D11 texture `handle` (a handle of the kind D3D11_TEXTURE_KMT, width x height, R8G8B8A8) as an image of Vulkan. */
static fire_gfx_int fire_gfx_gpuImport(FireGfxGpuTarget* g, void* handle, fire_gfx_int width, fire_gfx_int height) {
    VkImageCreateInfo ii;
    VkExternalMemoryImageCreateInfo ext;
    VkMemoryRequirements req;
    VkMemoryAllocateInfo ai;
    VkMemoryDedicatedAllocateInfo ded;
    FireGfxVkImportWin32 imp;
    FireGfxVkWin32Props props;
    fire_gfx_int type = -1, k;
    fire_gfx_gpuFreeImport(g);
    memset(&ext, 0, sizeof ext);
    ext.sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO;
    ext.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT;
    memset(&ii, 0, sizeof ii);
    ii.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ii.pNext = &ext;
    ii.imageType = VK_IMAGE_TYPE_2D;
    ii.format = VK_FORMAT_R8G8B8A8_UNORM;
    ii.extent.width = (uint32_t)width; ii.extent.height = (uint32_t)height; ii.extent.depth = 1;
    ii.mipLevels = 1; ii.arrayLayers = 1;
    ii.samples = VK_SAMPLE_COUNT_1_BIT;
    ii.tiling = VK_IMAGE_TILING_OPTIMAL;
    ii.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    ii.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (g_fireGfxVk.vkCreateImage(g_fireGfxVk.device, &ii, NULL, &g->dcImage) != VK_SUCCESS) { g->dcImage = 0; return 0; }
    g_fireGfxVk.vkGetImageMemoryRequirements(g_fireGfxVk.device, g->dcImage, &req);
    memset(&props, 0, sizeof props);
    props.sType = (VkStructureType)1000073002;   /* VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR */
    if (g_fireGfxVk.getWin32HandleProps(g_fireGfxVk.device, VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT, handle, &props) != VK_SUCCESS) props.memoryTypeBits = req.memoryTypeBits;
    for (k = 0; k < (fire_gfx_int)g_fireGfxVk.memProps.memoryTypeCount && type < 0; k++)
        if ((req.memoryTypeBits & props.memoryTypeBits & (1u << k)) && (g_fireGfxVk.memProps.memoryTypes[k].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) type = k;
    for (k = 0; k < (fire_gfx_int)g_fireGfxVk.memProps.memoryTypeCount && type < 0; k++)
        if (req.memoryTypeBits & props.memoryTypeBits & (1u << k)) type = k;
    if (type < 0) { fire_gfx_gpuFreeImport(g); return 0; }
    memset(&ded, 0, sizeof ded);
    ded.sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO;
    ded.image = g->dcImage;
    memset(&imp, 0, sizeof imp);
    imp.sType = (VkStructureType)1000073000;   /* VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR */
    imp.pNext = &ded;
    imp.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT;
    imp.handle = handle;
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.pNext = &imp;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = (uint32_t)type;
    if (g_fireGfxVk.vkAllocateMemory(g_fireGfxVk.device, &ai, NULL, &g->dcMemory) != VK_SUCCESS) { g->dcMemory = 0; fire_gfx_gpuFreeImport(g); return 0; }
    if (g_fireGfxVk.vkBindImageMemory(g_fireGfxVk.device, g->dcImage, g->dcMemory, 0) != VK_SUCCESS) { fire_gfx_gpuFreeImport(g); return 0; }
    g->dcHandle = handle; g->dcW = width; g->dcH = height; g->dcReady = 0;
    return 1;
}

/* The last frame into a window of DirectComposition (the lock is held): copied into the window's picture (imported), then the bridge presents it. 1: shown. */
static fire_gfx_int fire_gfx_gpuPresentDcompLocked(FireGfxGpuTarget* g, const fire_native_window* w) {
    fire_dcomp_bridge* b = (fire_dcomp_bridge*)w->a;
    VkCommandBufferBeginInfo bi;
    VkSubmitInfo si;
    VkImageCopy region;
    void* handle;
    uint8_t luid[8];
    if (!g_fireGfxVk.interop || b == NULL || b->size < (int32_t)sizeof *b || b->texture == NULL || b->present == NULL || !g->hasPicture) return 0;
    memcpy(luid, &b->luidLow, 4); memcpy(luid + 4, &b->luidHigh, 4);
    if (memcmp(luid, g_fireGfxVk.luid, 8) != 0) return 0;   /* (D3D11 is on another adapter) */
    if (g->dcomp && !fire_gfx_gpuSameWindow(&g->win, w)) return 0;   /* (one window per presenter) */
    if (!fire_gfx_gpuTargetCommands(g)) return 0;
    fire_gfx_gpuWaitTarget(g);
    handle = b->texture(b, g->pic.width, g->pic.height);
    if (handle == NULL) return 0;
    if ((handle != g->dcHandle || g->dcW != g->pic.width || g->dcH != g->pic.height) && !fire_gfx_gpuImport(g, handle, g->pic.width, g->pic.height)) return 0;
    g->dcomp = 1; g->win = *w;
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (g_fireGfxVk.vkResetCommandBuffer(g->cmd, 0) != VK_SUCCESS || g_fireGfxVk.vkBeginCommandBuffer(g->cmd, &bi) != VK_SUCCESS) return 0;
    /* the picture from D3D11 (the first time: it is new), the frame copied into it, back to D3D11 */
    if (g->dcReady) fire_gfx_gpuShareBarrier(g->cmd, g->dcImage, VK_IMAGE_LAYOUT_GENERAL, VK_QUEUE_FAMILY_EXTERNAL, g_fireGfxVk.family, 0, VK_ACCESS_TRANSFER_WRITE_BIT);
    else fire_gfx_gpuShareBarrier(g->cmd, g->dcImage, VK_IMAGE_LAYOUT_UNDEFINED, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED, 0, VK_ACCESS_TRANSFER_WRITE_BIT);
    fire_gfx_gpuImageBarrier(g->cmd, g->pic.image, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_TRANSFER_READ_BIT,
                             VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    memset(&region, 0, sizeof region);
    region.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; region.srcSubresource.layerCount = 1;
    region.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; region.dstSubresource.layerCount = 1;
    region.extent.width = (uint32_t)g->pic.width; region.extent.height = (uint32_t)g->pic.height; region.extent.depth = 1;
    g_fireGfxVk.vkCmdCopyImage(g->cmd, g->pic.image, VK_IMAGE_LAYOUT_GENERAL, g->dcImage, VK_IMAGE_LAYOUT_GENERAL, 1, &region);
    fire_gfx_gpuShareBarrier(g->cmd, g->dcImage, VK_IMAGE_LAYOUT_GENERAL, g_fireGfxVk.family, VK_QUEUE_FAMILY_EXTERNAL, VK_ACCESS_TRANSFER_WRITE_BIT, 0);
    if (g_fireGfxVk.vkEndCommandBuffer(g->cmd) != VK_SUCCESS) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1; si.pCommandBuffers = &g->cmd;
    if (g_fireGfxVk.vkResetFences(g_fireGfxVk.device, 1, &g->fence) != VK_SUCCESS) return 0;
    if (g_fireGfxVk.vkQueueSubmit(g_fireGfxVk.queue, 1, &si, g->fence) != VK_SUCCESS) return 0;
    g->fencePending = 1;
    fire_gfx_gpuWaitTarget(g);   /* (D3D11 copies it next) */
    g->dcReady = 1;
    return b->present(b) != 0;
}

static fire_gfx_int fire_gfx_gpuPresentLocked(FireGfxGpuTarget* g, const fire_native_window* w) {
    uint32_t index = 0;
    fire_gfx_int made, attempt;
    VkResult r = VK_SUCCESS;
    VkCommandBufferBeginInfo bi;
    VkImageBlit blit;
    VkSubmitInfo si;
    VkPresentInfoKHR pi;
    VkPipelineStageFlags waitStage = VK_PIPELINE_STAGE_TRANSFER_BIT;
    if (w->kind == FIRE_NATIVE_DCOMP) return fire_gfx_gpuPresentDcompLocked(g, w);
    if (!g->hasPicture) return 0;
    if (g->surface && !fire_gfx_gpuSameWindow(&g->win, w)) return 0;   /* (another window shows the same framebuffer: one window per presenter - that one shows the pixels) */
    if (!fire_gfx_gpuTargetCommands(g)) return 0;
    if (!g->surface && !fire_gfx_gpuMakeSurface(g, w)) return 0;
    fire_gfx_gpuWaitTarget(g);
    for (attempt = 0; ; attempt++) {
        made = fire_gfx_gpuMakeSwapchain(g, w);
        if (made == 0) return 0;
        if (made == 2) return 1;
        r = g_fireGfxVk.vkAcquireNextImageKHR(g_fireGfxVk.device, g->swapchain, ~(uint64_t)0, g->acquired, 0, &index);
        if (r == VK_ERROR_OUT_OF_DATE_KHR && attempt == 0) { g->stale = 1; continue; }
        break;
    }
    if (r == VK_SUBOPTIMAL_KHR) g->stale = 1;
    else if (r != VK_SUCCESS) return 0;
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (g_fireGfxVk.vkResetCommandBuffer(g->cmd, 0) != VK_SUCCESS || g_fireGfxVk.vkBeginCommandBuffer(g->cmd, &bi) != VK_SUCCESS) return 0;
    /* the frame (copied by the program's thread before, on the same queue) is read; the image of the swap chain is written once the acquire is done */
    fire_gfx_gpuImageBarrier(g->cmd, g->pic.image, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_TRANSFER_READ_BIT,
                             VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    fire_gfx_gpuImageBarrier(g->cmd, g->images[index], VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
                             VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    memset(&blit, 0, sizeof blit);
    blit.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; blit.srcSubresource.layerCount = 1;
    blit.srcOffsets[1].x = g->pic.width; blit.srcOffsets[1].y = g->pic.height; blit.srcOffsets[1].z = 1;
    blit.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; blit.dstSubresource.layerCount = 1;
    blit.dstOffsets[1].x = (int32_t)g->extent.width; blit.dstOffsets[1].y = (int32_t)g->extent.height; blit.dstOffsets[1].z = 1;
    g_fireGfxVk.vkCmdBlitImage(g->cmd, g->pic.image, VK_IMAGE_LAYOUT_GENERAL, g->images[index], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &blit, VK_FILTER_NEAREST);
    fire_gfx_gpuImageBarrier(g->cmd, g->images[index], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, VK_ACCESS_TRANSFER_WRITE_BIT, 0,
                             VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
    if (g_fireGfxVk.vkEndCommandBuffer(g->cmd) != VK_SUCCESS) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = 1; si.pWaitSemaphores = &g->acquired; si.pWaitDstStageMask = &waitStage;
    si.commandBufferCount = 1; si.pCommandBuffers = &g->cmd;
    si.signalSemaphoreCount = 1; si.pSignalSemaphores = &g->rendered[index];
    if (g_fireGfxVk.vkResetFences(g_fireGfxVk.device, 1, &g->fence) != VK_SUCCESS) return 0;
    if (g_fireGfxVk.vkQueueSubmit(g_fireGfxVk.queue, 1, &si, g->fence) != VK_SUCCESS) return 0;
    g->fencePending = 1;
    memset(&pi, 0, sizeof pi);
    pi.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    pi.waitSemaphoreCount = 1; pi.pWaitSemaphores = &g->rendered[index];
    pi.swapchainCount = 1; pi.pSwapchains = &g->swapchain; pi.pImageIndices = &index;
    r = g_fireGfxVk.vkQueuePresentKHR(g_fireGfxVk.queue, &pi);
    if (r == VK_ERROR_OUT_OF_DATE_KHR || r == VK_SUBOPTIMAL_KHR) { g->stale = 1; return 1; }
    return r == VK_SUCCESS;
}

/* The presenter of the windows that show an accelerated framebuffer (a fire_present_fn, on the window's thread): op 1 shows the last frame - `pixels` are not needed -, op 0
 * lets go of the window. */
static int32_t fire_gfx_gpuPresent(void* ctx, int32_t op, const fire_native_window* w, const uint32_t* pixels, int32_t width, int32_t height) {
    FireGfxGpuTarget* g = (FireGfxGpuTarget*)ctx;
    fire_gfx_int ok = 0;
    (void)pixels; (void)width; (void)height;
    fire_gfx_gpuLock();
    if (op == 0) {
        if (w == NULL || !fire_gfx_gpuHolds(g) || fire_gfx_gpuSameWindow(&g->win, w)) {
            if (g_fireGfxVk.state == 1) fire_gfx_gpuFreeSurface(g);
            if (g->works == 1) g->works = 0;   /* (the frames are read back again until a window takes them) */
        }
        fire_gfx_gpuUnlock();
        return 1;
    }
    if (g_fireGfxVk.state == 1 && w != NULL && (w->kind == FIRE_NATIVE_DCOMP ? g_fireGfxVk.interop : g_fireGfxVk.wsi)) {
        fire_gfx_int noAlpha = g->noAlpha;
        ok = fire_gfx_gpuPresentLocked(g, w);
        if (ok) g->works = 1;
        else if (g->noAlpha && !noAlpha) { fire_gfx_gpuFreeSurface(g); g->works = 0; }   /* (only the frames that mix go as pixels; this window takes pixels from now on) */
        else if (!fire_gfx_gpuHolds(g) || fire_gfx_gpuSameWindow(&g->win, w)) { fire_gfx_gpuFreeSurface(g); g->works = -1; }
        else g->others = 1;
    }
    fire_gfx_gpuUnlock();
    return ok;
}

#endif
#endif
