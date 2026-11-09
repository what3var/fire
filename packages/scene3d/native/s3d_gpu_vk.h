/* fire scene3d: the hardware backend - the queue of s3d_engine.h drawn by the GPU through Vulkan (docs/graphics/3d.md, "Hardware").
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
 * with the mesh or texture. Such a flush waits for the GPU: one picture at a time.
 *
 * DirectDrawing (s3d_gpu_attach): the picture stays on the GPU and goes into the window straight away. The windows that show the camera's framebuffer call the presenter of the
 * camera (s3d_gpu_present, a fire_present_fn) on their own thread instead of showing the framebuffer: it draws the picture into the window through a swap chain (VK_KHR_swapchain on a
 * surface for Win32, X11, Wayland or a CAMetalLayer) and lays the framebuffer over it where its alpha is not 0 - what the program drew there after Render. Nothing is read back, and
 * a flush does not wait for the GPU: the next one does (the program goes on meanwhile). The first picture is read back as well (the window may not take a presenter: then nothing
 * changes); a presenter that fails gives the window back, and the camera reads its pictures back again.
 *
 * Switches (environment): FIRE_S3D_GPU=0 never uses the GPU; FIRE_S3D_GPU=cpu also takes a Vulkan device that is a CPU (lavapipe, SwiftShader - slower than the software renderer, but
 * a GPU-less machine can test the backend with it). */
#ifndef FIRE_S3D_GPU_VK_H
#define FIRE_S3D_GPU_VK_H

#if defined(FIRE_S3D_NO_GPU) || defined(__EMSCRIPTEN__) || defined(ESP_PLATFORM) || !(defined(_WIN32) || defined(__linux__) || defined(__APPLE__) || defined(__FreeBSD__))

/* ---- no hardware backend on this target ---- */
static int s3d_gpu_flush(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st) {
    (void)rgba; (void)f; (void)cam; (void)lights; (void)lightCount; (void)st;
    return 0;
}
static void s3d_gpu_releaseMesh(S3dMesh* m) { (void)m; }
static void s3d_gpu_releaseTexture(S3dTexture* t) { (void)t; }
static void s3d_gpu_releaseFrame(S3dFrame* f) { (void)f; }
static void s3d_gpu_shutdown(void) {}
static const char* s3d_gpu_deviceName(void) { return ""; }
static const char* s3d_gpu_error(void) { return "This target has no hardware renderer."; }
static int s3d_gpu_attach(S3dFrame* f, int64_t framebuffer) { (void)f; (void)framebuffer; return 0; }
static void s3d_gpu_detach(S3dFrame* f) { (void)f; }

#else

#ifdef FIRE_S3D_VK_SYSTEM_HEADERS
#include <vulkan/vulkan.h>       /* (to check s3d_vk.h against the real header) */
#else
#ifndef FIRE_VK_DECLS_H
#include "s3d_vk.h"   /* (the same file as graphics/native/fire_gfx_vk.h: a native build with both has them once) */
#endif
#endif
#ifndef FIRE_S3D_SHADERS_H
#include "s3d_shaders.h"
#endif
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#include <pthread.h>
#endif

/* The window of a package's presenter (the same declarations as in native/abi/fire_pkg_abi.h, where they are described). */
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

/* The surfaces of the window systems (their declarations are in the platform headers of Vulkan, vulkan_win32.h & co., which need the headers of the window systems: the
 * structures are written out here; tools/check_s3d_vk.sh compares them with the real ones where the headers are there). One function type makes all of them. */
typedef struct S3dVkWin32SurfaceCreateInfo { VkStructureType sType; const void* pNext; VkFlags flags; void* hinstance; void* hwnd; } S3dVkWin32SurfaceCreateInfo;
typedef struct S3dVkXlibSurfaceCreateInfo { VkStructureType sType; const void* pNext; VkFlags flags; void* dpy; unsigned long window; } S3dVkXlibSurfaceCreateInfo;
typedef struct S3dVkWaylandSurfaceCreateInfo { VkStructureType sType; const void* pNext; VkFlags flags; void* display; void* surface; } S3dVkWaylandSurfaceCreateInfo;
typedef struct S3dVkMetalSurfaceCreateInfo { VkStructureType sType; const void* pNext; VkFlags flags; const void* pLayer; } S3dVkMetalSurfaceCreateInfo;
#define S3D_VK_STRUCTURE_TYPE_XLIB_SURFACE_CREATE_INFO_KHR ((VkStructureType)1000004000)
#define S3D_VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR ((VkStructureType)1000006000)
#define S3D_VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR ((VkStructureType)1000009000)
#define S3D_VK_STRUCTURE_TYPE_METAL_SURFACE_CREATE_INFO_EXT ((VkStructureType)1000217000)
typedef VkResult (VKAPI_PTR *PFN_s3dCreateSurface)(VkInstance instance, const void* createInfo, const VkAllocationCallbacks* allocator, VkSurfaceKHR* surface);

/* One lock for the backend: the program's thread draws (flush), the windows' threads present, and both use the one queue. */
#ifdef _WIN32
static SRWLOCK g_s3dGpuLock = SRWLOCK_INIT;
static void s3d_gpu_lock(void) { AcquireSRWLockExclusive(&g_s3dGpuLock); }
static void s3d_gpu_unlock(void) { ReleaseSRWLockExclusive(&g_s3dGpuLock); }
#else
static pthread_mutex_t g_s3dGpuLock = PTHREAD_MUTEX_INITIALIZER;
static void s3d_gpu_lock(void) { pthread_mutex_lock(&g_s3dGpuLock); }
static void s3d_gpu_unlock(void) { pthread_mutex_unlock(&g_s3dGpuLock); }
#endif

/* The functions the backend calls: loaded from the instance (I) and the device (D). */
#define S3D_VK_INSTANCE_FUNCTIONS(X) \
    X(vkDestroyInstance) X(vkEnumeratePhysicalDevices) X(vkGetPhysicalDeviceProperties) X(vkGetPhysicalDeviceQueueFamilyProperties) \
    X(vkGetPhysicalDeviceMemoryProperties) X(vkGetPhysicalDeviceFormatProperties) X(vkCreateDevice) X(vkGetDeviceProcAddr) X(vkEnumerateDeviceExtensionProperties)
#define S3D_VK_DEVICE_FUNCTIONS(X) \
    X(vkDestroyDevice) X(vkGetDeviceQueue) X(vkDeviceWaitIdle) X(vkQueueWaitIdle) X(vkCreateSemaphore) X(vkDestroySemaphore) X(vkCreateCommandPool) X(vkDestroyCommandPool) X(vkAllocateCommandBuffers) \
    X(vkBeginCommandBuffer) X(vkEndCommandBuffer) X(vkResetCommandBuffer) X(vkQueueSubmit) X(vkCreateFence) X(vkDestroyFence) X(vkWaitForFences) X(vkResetFences) \
    X(vkCreateBuffer) X(vkDestroyBuffer) X(vkGetBufferMemoryRequirements) X(vkAllocateMemory) X(vkFreeMemory) X(vkBindBufferMemory) X(vkMapMemory) X(vkUnmapMemory) \
    X(vkInvalidateMappedMemoryRanges) X(vkCreateImage) X(vkDestroyImage) X(vkGetImageMemoryRequirements) X(vkBindImageMemory) X(vkCreateImageView) X(vkDestroyImageView) \
    X(vkCreateSampler) X(vkDestroySampler) X(vkCreateShaderModule) X(vkDestroyShaderModule) X(vkCreateDescriptorSetLayout) X(vkDestroyDescriptorSetLayout) \
    X(vkCreatePipelineLayout) X(vkDestroyPipelineLayout) X(vkCreateDescriptorPool) X(vkDestroyDescriptorPool) X(vkAllocateDescriptorSets) X(vkFreeDescriptorSets) \
    X(vkUpdateDescriptorSets) X(vkCreateRenderPass) X(vkDestroyRenderPass) X(vkCreateFramebuffer) X(vkDestroyFramebuffer) X(vkCreateGraphicsPipelines) X(vkDestroyPipeline) \
    X(vkCmdBeginRenderPass) X(vkCmdEndRenderPass) X(vkCmdBindPipeline) X(vkCmdBindDescriptorSets) X(vkCmdBindVertexBuffers) X(vkCmdBindIndexBuffer) X(vkCmdDrawIndexed) \
    X(vkCmdSetViewport) X(vkCmdSetScissor) X(vkCmdPushConstants) X(vkCmdPipelineBarrier) X(vkCmdCopyBufferToImage) X(vkCmdCopyImageToBuffer) X(vkCmdCopyBuffer) X(vkCmdDraw)
/* For DirectDrawing, where the driver has them (VK_KHR_surface, VK_KHR_swapchain): loaded when they are there. */
#define S3D_VK_WSI_INSTANCE_FUNCTIONS(X) \
    X(vkDestroySurfaceKHR) X(vkGetPhysicalDeviceSurfaceSupportKHR) X(vkGetPhysicalDeviceSurfaceCapabilitiesKHR) X(vkGetPhysicalDeviceSurfaceFormatsKHR) \
    X(vkGetPhysicalDeviceSurfacePresentModesKHR)
#define S3D_VK_WSI_DEVICE_FUNCTIONS(X) \
    X(vkCreateSwapchainKHR) X(vkDestroySwapchainKHR) X(vkGetSwapchainImagesKHR) X(vkAcquireNextImageKHR) X(vkQueuePresentKHR)

/* What the shaders read (matrices row major, as the engine keeps them). Keep in step with shaders/s3d_common.glsl. The frame is a uniform block (std140); the lights and the
 * shadow maps are storage buffers (std430) of any length - there is no limit on their number but the memory of the GPU. */
typedef struct S3dGpuLightU { int32_t info[4]; float color[4]; float pos[4]; float toLight[4]; } S3dGpuLightU;   /* info: kind, mask, first shadow map (-1: none) */
typedef struct S3dGpuMapU { float view[16]; float p[4]; float q[4]; float r[4]; int32_t t[4]; } S3dGpuMapU;     /* p: sx sy ox oy; q: f res ortho; r: dmin 1/range far; t: x y layer */
typedef struct S3dGpuFrameU {
    float vp[16];
    float eye[4], ambient[4];
    int32_t counts[4];                         /* the lights */
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
typedef struct S3dGpuFrame {
    int w, h;
    S3dVkImage color, depth;
    VkFramebuffer fb;
    S3dVkBuffer readback;
    int hasPicture;              /* a picture was drawn into `color` */
    VkImageLayout colorLayout;   /* the layout of `color` after the commands submitted so far */
} S3dGpuFrame;

/* A camera that draws into the windows of its framebuffer (DirectDrawing): what a window needs to show its pictures. It is never freed while the backend lives (a window's thread may
 * still hold it): it is detached, and its presenter answers 0 then. */
typedef struct S3dGpuTarget {
    int attached;
    int64_t framebuffer;
    S3dFrame* frame;
    int works;                   /* 1: the presenter showed a picture; 0: not yet (the camera reads its pictures back as well); -1: it failed */
    fire_native_window win;      /* the window of the surface */
    VkSurfaceKHR surface;
    VkSwapchainKHR swapchain;
    VkFormat format;
    VkExtent2D extent;
    int vsync, stale;
    uint32_t imageCount;
    VkImage* images; VkImageView* views; VkFramebuffer* fbs; VkSemaphore* rendered;
    VkSemaphore acquired;
    VkCommandBuffer cmd;
    VkFence fence;
    int fencePending;
    VkRenderPass pass;
    VkPipeline pipe;
    VkDescriptorSet set; VkDescriptorPool pool;
    S3dVkImage overlay; int overlayW, overlayH;
    S3dVkBuffer staging;
    struct S3dGpuTarget* next;
} S3dGpuTarget;
/* A shadow map in the atlas: its size, its corner, its layer (layer -1: it did not fit, the light casts no shadow). */
typedef struct S3dGpuTile { int res, x, y, layer, order; } S3dGpuTile;

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
    S3D_VK_WSI_INSTANCE_FUNCTIONS(S3D_VK_DECLARE)
    S3D_VK_WSI_DEVICE_FUNCTIONS(S3D_VK_DECLARE)
#undef S3D_VK_DECLARE
    PFN_s3dCreateSurface createWin32Surface, createXlibSurface, createWaylandSurface, createMetalSurface;
    int wsi;                   /* the instance has VK_KHR_surface and the device VK_KHR_swapchain: DirectDrawing is possible */
    int ended;                 /* the program ends (s3d_gpu_atExit): Vulkan is not touched any more */
    int pending;               /* a flush was submitted and not waited for (DirectDrawing) */
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
    S3dVkBuffer frameUbo, objectUbo, lightBuf, mapBuf, staging;
    /* the atlas of the shadow maps: `shadowLayers` layers of shadowSize x shadowSize; the maps of a picture are packed into it (S3dGpuTile) */
    S3dVkImage shadowColor, shadowDepth;
    VkImageView* layerViews; VkFramebuffer* layerFbs;
    int shadowSize, shadowLayers;
    VkDeviceSize shadowBudget;   /* the most memory the shadow maps take (a part of the GPU's memory) */
    S3dGpuMapU* mapData; struct S3dGpuTile* tiles; size_t mapCap;
    S3dGpuFrameU frameData;
    int* order; size_t orderCap;
    /* DirectDrawing */
    VkPipelineLayout layoutPresent;
    VkShaderModule presentVert, presentFrag;
    VkSampler clamp;
    S3dGpuTarget* targets;
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
    const char* enabled[8];
    uint32_t n = 0;
    int surface = 0;
    VkResult r;
    memset(&app, 0, sizeof app);
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.pApplicationName = "fire scene3d";
    app.pEngineName = "fire scene3d";
    app.apiVersion = (uint32_t)1 << 22;   /* Vulkan 1.0 */
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ci.pApplicationInfo = &app;
    if (g_s3dVk.vkEnumerateInstanceExtensionProperties(NULL, &count, NULL) == VK_SUCCESS && count > 0) {
        exts = (VkExtensionProperties*)malloc(sizeof(VkExtensionProperties) * count);
        if (exts && g_s3dVk.vkEnumerateInstanceExtensionProperties(NULL, &count, exts) == VK_SUCCESS) {
            static const char* const surfaces[4] = {"VK_KHR_win32_surface", "VK_KHR_xlib_surface", "VK_KHR_wayland_surface", "VK_EXT_metal_surface"};
            int k;
            /* MoltenVK (macOS) is a "portability" implementation: it is only listed when the program says it can take one */
            if (s3d_gpu_hasExtension(exts, count, "VK_KHR_portability_enumeration")) {
                enabled[n++] = "VK_KHR_portability_enumeration";
                ci.flags = 0x00000001;   /* VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR */
            }
            /* the surfaces of the window systems (DirectDrawing), where they are there */
            if (s3d_gpu_hasExtension(exts, count, "VK_KHR_surface")) {
                enabled[n++] = "VK_KHR_surface";
                for (k = 0; k < 4; k++) if (s3d_gpu_hasExtension(exts, count, surfaces[k])) { enabled[n++] = surfaces[k]; surface = 1; }
            }
        }
        free(exts);
    }
    ci.enabledExtensionCount = n;
    ci.ppEnabledExtensionNames = n > 0 ? enabled : NULL;
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
    if (surface) {
        g_s3dVk.wsi = 1;
#define S3D_VK_LOAD_W(name) g_s3dVk.name = (PFN_##name)g_s3dVk.vkGetInstanceProcAddr(g_s3dVk.instance, #name); if (g_s3dVk.name == NULL) g_s3dVk.wsi = 0;
        S3D_VK_WSI_INSTANCE_FUNCTIONS(S3D_VK_LOAD_W)
#undef S3D_VK_LOAD_W
        g_s3dVk.createWin32Surface = (PFN_s3dCreateSurface)g_s3dVk.vkGetInstanceProcAddr(g_s3dVk.instance, "vkCreateWin32SurfaceKHR");
        g_s3dVk.createXlibSurface = (PFN_s3dCreateSurface)g_s3dVk.vkGetInstanceProcAddr(g_s3dVk.instance, "vkCreateXlibSurfaceKHR");
        g_s3dVk.createWaylandSurface = (PFN_s3dCreateSurface)g_s3dVk.vkGetInstanceProcAddr(g_s3dVk.instance, "vkCreateWaylandSurfaceKHR");
        g_s3dVk.createMetalSurface = (PFN_s3dCreateSurface)g_s3dVk.vkGetInstanceProcAddr(g_s3dVk.instance, "vkCreateMetalSurfaceEXT");
    }
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
    const char* enabled[2];
    uint32_t n = 0;
    int swapchain = 0;
    memset(&qi, 0, sizeof qi);
    qi.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qi.queueFamilyIndex = g_s3dVk.family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &priority;
    memset(&di, 0, sizeof di);
    di.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    di.queueCreateInfoCount = 1;
    di.pQueueCreateInfos = &qi;
    if (g_s3dVk.vkEnumerateDeviceExtensionProperties(g_s3dVk.phys, NULL, &count, NULL) == VK_SUCCESS && count > 0) {
        exts = (VkExtensionProperties*)malloc(sizeof(VkExtensionProperties) * count);
        if (exts && g_s3dVk.vkEnumerateDeviceExtensionProperties(g_s3dVk.phys, NULL, &count, exts) == VK_SUCCESS) {
            /* a portability implementation has to be told that the program knows it is one */
            if (s3d_gpu_hasExtension(exts, count, "VK_KHR_portability_subset")) enabled[n++] = "VK_KHR_portability_subset";
            /* the swap chains of DirectDrawing */
            if (g_s3dVk.wsi && s3d_gpu_hasExtension(exts, count, "VK_KHR_swapchain")) { enabled[n++] = "VK_KHR_swapchain"; swapchain = 1; }
        }
        free(exts);
    }
    if (!swapchain) g_s3dVk.wsi = 0;
    di.enabledExtensionCount = n;
    di.ppEnabledExtensionNames = n > 0 ? enabled : NULL;
    if (!s3d_gpu_check(g_s3dVk.vkCreateDevice(g_s3dVk.phys, &di, NULL, &g_s3dVk.device), "vkCreateDevice")) { g_s3dVk.device = NULL; return 0; }
#define S3D_VK_LOAD_D(name) \
    g_s3dVk.name = (PFN_##name)g_s3dVk.vkGetDeviceProcAddr(g_s3dVk.device, #name); \
    if (g_s3dVk.name == NULL) { s3d_gpu_fail("The Vulkan driver lacks " #name "."); return 0; }
    S3D_VK_DEVICE_FUNCTIONS(S3D_VK_LOAD_D)
#undef S3D_VK_LOAD_D
    if (g_s3dVk.wsi) {
#define S3D_VK_LOAD_WD(name) g_s3dVk.name = (PFN_##name)g_s3dVk.vkGetDeviceProcAddr(g_s3dVk.device, #name); if (g_s3dVk.name == NULL) g_s3dVk.wsi = 0;
        S3D_VK_WSI_DEVICE_FUNCTIONS(S3D_VK_LOAD_WD)
#undef S3D_VK_LOAD_WD
    }
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
    if (g_s3dVk.device == NULL || g_s3dVk.ended) return;
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
    if (g_s3dVk.device == NULL || g_s3dVk.ended) return;
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
/* Waits for the flush that was submitted without waiting (DirectDrawing): its command buffer and the buffers it reads are free again. */
static int s3d_gpu_waitPending(void) {
    if (!g_s3dVk.pending) return 1;
    g_s3dVk.pending = 0;
    return s3d_gpu_check(g_s3dVk.vkWaitForFences(g_s3dVk.device, 1, &g_s3dVk.fence, VK_TRUE, UINT64_MAX), "vkWaitForFences");
}
/* Waits until the GPU has done everything (before something it may still use is freed). */
static void s3d_gpu_idle(void) {
    if (g_s3dVk.state != 1) return;
    s3d_gpu_waitPending();
    g_s3dVk.vkQueueWaitIdle(g_s3dVk.queue);
}

static int s3d_gpu_begin(void) {
    VkCommandBufferBeginInfo bi;
    if (!s3d_gpu_waitPending()) return 0;
    if (!s3d_gpu_check(g_s3dVk.vkResetCommandBuffer(g_s3dVk.cmd, 0), "vkResetCommandBuffer")) return 0;
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    return s3d_gpu_check(g_s3dVk.vkBeginCommandBuffer(g_s3dVk.cmd, &bi), "vkBeginCommandBuffer");
}
/* Submits the commands; `wait`: and waits for them, else the next use of the command buffer does (s3d_gpu_waitPending). */
static int s3d_gpu_submit(int wait) {
    VkSubmitInfo si;
    if (!s3d_gpu_check(g_s3dVk.vkEndCommandBuffer(g_s3dVk.cmd), "vkEndCommandBuffer")) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &g_s3dVk.cmd;
    if (!s3d_gpu_check(g_s3dVk.vkResetFences(g_s3dVk.device, 1, &g_s3dVk.fence), "vkResetFences")) return 0;
    if (!s3d_gpu_check(g_s3dVk.vkQueueSubmit(g_s3dVk.queue, 1, &si, g_s3dVk.fence), "vkQueueSubmit")) return 0;
    if (!wait) { g_s3dVk.pending = 1; return 1; }
    return s3d_gpu_check(g_s3dVk.vkWaitForFences(g_s3dVk.device, 1, &g_s3dVk.fence, VK_TRUE, UINT64_MAX), "vkWaitForFences");
}
static int s3d_gpu_submitAndWait(void) { return s3d_gpu_submit(1); }

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
    VkDescriptorSetLayoutBinding b[5];
    VkDescriptorSetLayoutCreateInfo li;
    VkPipelineLayoutCreateInfo pi;
    VkDescriptorSetLayout sets[2];
    VkPushConstantRange pc;
    VkDescriptorPoolSize sizes[4];
    VkDescriptorPoolCreateInfo dp;
    VkDescriptorSetAllocateInfo ai;
    /* set 0, the picture: the uniforms of the frame (camera), the atlas of the shadow maps, the uniforms of the object (dynamic offset), the lights, the shadow maps */
    memset(b, 0, sizeof b);
    b[0].binding = 0; b[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER; b[0].descriptorCount = 1; b[0].stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    b[1].binding = 1; b[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; b[1].descriptorCount = 1; b[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    b[2].binding = 2; b[2].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC; b[2].descriptorCount = 1; b[2].stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    b[3].binding = 3; b[3].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; b[3].descriptorCount = 1; b[3].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    b[4].binding = 4; b[4].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; b[4].descriptorCount = 1; b[4].stageFlags = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    memset(&li, 0, sizeof li);
    li.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    li.bindingCount = 5;
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
    sizes[3].type = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; sizes[3].descriptorCount = 2;
    memset(&dp, 0, sizeof dp);
    dp.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    dp.maxSets = 1;
    dp.poolSizeCount = 4;
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
    VkDescriptorBufferInfo frameInfo, objectInfo, lightInfo, mapInfo;
    VkDescriptorImageInfo shadowInfo;
    VkWriteDescriptorSet w[5];
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
    lightInfo.buffer = g_s3dVk.lightBuf.buf; lightInfo.offset = 0; lightInfo.range = VK_WHOLE_SIZE;
    mapInfo.buffer = g_s3dVk.mapBuf.buf; mapInfo.offset = 0; mapInfo.range = VK_WHOLE_SIZE;
    w[3].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET; w[3].dstSet = g_s3dVk.frameSet; w[3].dstBinding = 3; w[3].descriptorCount = 1;
    w[3].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; w[3].pBufferInfo = &lightInfo;
    w[4].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET; w[4].dstSet = g_s3dVk.frameSet; w[4].dstBinding = 4; w[4].descriptorCount = 1;
    w[4].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER; w[4].pBufferInfo = &mapInfo;
    g_s3dVk.vkUpdateDescriptorSets(g_s3dVk.device, 5, w, 0, NULL);
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
    if (g_s3dVk.ended) return;
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
    size_t n = mesh->posN / 3, i, ni = mesh->idxN / 3 * 3;
    float* v;
    uint32_t* ix;
    if (g) return g;
    g = (S3dGpuMesh*)calloc(1, sizeof(S3dGpuMesh));
    v = (float*)malloc(n * 8 * sizeof(float) + 4);
    ix = (uint32_t*)malloc(ni * sizeof(uint32_t) + 4);
    if (g == NULL || v == NULL || ix == NULL) { free(g); free(v); free(ix); return NULL; }
    /* (the mesh is compact in memory: the GPU gets it as floats and 32-bit indices) */
    for (i = 0; i < n; i++) {
        S3dVec3 p = s3d_meshPos(mesh, i), nr = s3d_meshNrm(mesh, i);
        v[i * 8 + 0] = p.x; v[i * 8 + 1] = p.y; v[i * 8 + 2] = p.z;
        v[i * 8 + 3] = nr.x; v[i * 8 + 4] = nr.y; v[i * 8 + 5] = nr.z;
        s3d_meshUv(mesh, i, &v[i * 8 + 6], &v[i * 8 + 7]);
    }
    for (i = 0; i < ni; i++) ix[i] = s3d_meshIdx(mesh, i);
    if (!s3d_gpu_deviceBuffer(&g->vb, v, n * 8 * sizeof(float), VK_BUFFER_USAGE_VERTEX_BUFFER_BIT, VK_ACCESS_VERTEX_ATTRIBUTE_READ_BIT)
        || !s3d_gpu_deviceBuffer(&g->ib, ix, ni * sizeof(uint32_t), VK_BUFFER_USAGE_INDEX_BUFFER_BIT, VK_ACCESS_INDEX_READ_BIT)) {
        s3d_gpu_freeBuffer(&g->vb);
        free(v); free(ix); free(g);
        return NULL;
    }
    free(v); free(ix);
    g->indexCount = (uint32_t)(mesh->idxN / 3 * 3);
    mesh->gpu = g;
    return g;
}

/* ---- the targets of a frame: the picture, its depth, the buffer it is read back into ---- */
static void s3d_gpu_freeFrameTargets(S3dGpuFrame* g) {
    if (g_s3dVk.ended) return;
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
    if (g) { s3d_gpu_idle(); s3d_gpu_freeFrameTargets(g); }   /* (a window may still show the picture of the old size) */
    else {
        g = (S3dGpuFrame*)calloc(1, sizeof(S3dGpuFrame));
        if (g == NULL) return NULL;
        f->gpu = g;
    }
    if (!s3d_gpu_image(&g->color, (uint32_t)f->w, (uint32_t)f->h, 1, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_SAMPLED_BIT, VK_IMAGE_ASPECT_COLOR_BIT)
        || !s3d_gpu_image(&g->depth, (uint32_t)f->w, (uint32_t)f->h, 1, g_s3dVk.depthFormat, VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT, VK_IMAGE_ASPECT_DEPTH_BIT)
        || !s3d_gpu_framebuffer(g_s3dVk.passMain, g->color.view, g->depth.view, (uint32_t)f->w, (uint32_t)f->h, &g->fb)
        || !s3d_gpu_buffer(&g->readback, (VkDeviceSize)f->w * (VkDeviceSize)f->h * 4, VK_BUFFER_USAGE_TRANSFER_DST_BIT, 1, 1)) {
        s3d_gpu_freeFrameTargets(g);
        return NULL;
    }
    g->w = f->w; g->h = f->h;
    return g;
}

/* ---- the atlas of the shadow maps: an array of R32F layers (the distance to the light) and a depth buffer; a framebuffer per layer ---- */
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
    g_s3dVk.shadowSize = 0; g_s3dVk.shadowLayers = 0;
}

/* An atlas of `layers` layers of res x res (kept when it is big enough). */
static int s3d_gpu_shadows(int res, int layers) {
    int i;
    if (g_s3dVk.shadowSize == res && g_s3dVk.shadowLayers >= layers) return 1;
    s3d_gpu_freeShadows();
    if (!s3d_gpu_imageArray(&g_s3dVk.shadowColor, (uint32_t)res, (uint32_t)res, (uint32_t)layers, VK_FORMAT_R32_SFLOAT, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT, VK_IMAGE_ASPECT_COLOR_BIT, 1)
        || !s3d_gpu_image(&g_s3dVk.shadowDepth, (uint32_t)res, (uint32_t)res, 1, g_s3dVk.depthFormat, VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT, VK_IMAGE_ASPECT_DEPTH_BIT)) {
        s3d_gpu_freeShadows();
        return 0;
    }
    g_s3dVk.layerViews = (VkImageView*)calloc((size_t)layers, sizeof(VkImageView));
    g_s3dVk.layerFbs = (VkFramebuffer*)calloc((size_t)layers, sizeof(VkFramebuffer));
    g_s3dVk.shadowSize = res; g_s3dVk.shadowLayers = layers;
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
        || !s3d_gpu_hostBuffer(&g_s3dVk.objectUbo, g_s3dVk.objectStride * 16, VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT)
        || !s3d_gpu_hostBuffer(&g_s3dVk.lightBuf, sizeof(S3dGpuLightU) * 16, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT)
        || !s3d_gpu_hostBuffer(&g_s3dVk.mapBuf, sizeof(S3dGpuMapU) * 16, VK_BUFFER_USAGE_STORAGE_BUFFER_BIT)) return 0;
    {
        /* the shadow maps may take a quarter of the biggest memory of the GPU, at least 64 MB and at most 1 GB */
        VkDeviceSize biggest = 0;
        uint32_t h;
        for (h = 0; h < g_s3dVk.memProps.memoryHeapCount; h++)
            if ((g_s3dVk.memProps.memoryHeaps[h].flags & VK_MEMORY_HEAP_DEVICE_LOCAL_BIT) && g_s3dVk.memProps.memoryHeaps[h].size > biggest) biggest = g_s3dVk.memProps.memoryHeaps[h].size;
        g_s3dVk.shadowBudget = biggest / 4;
        if (g_s3dVk.shadowBudget < (VkDeviceSize)64 << 20) g_s3dVk.shadowBudget = (VkDeviceSize)64 << 20;
        if (g_s3dVk.shadowBudget > (VkDeviceSize)1 << 30) g_s3dVk.shadowBudget = (VkDeviceSize)1 << 30;
    }
    /* the texture of the objects without one: white, and flat */
    if (!s3d_gpu_image(&g_s3dVk.flatBump, 1, 1, 1, VK_FORMAT_R8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT, VK_IMAGE_ASPECT_COLOR_BIT)
        || !s3d_gpu_upload(g_s3dVk.flatBump.img, 1, 1, flat, 1)
        || !s3d_gpu_makeTex(&g_s3dVk.dummy, 1, 1, white, NULL, 0)) return 0;
    return s3d_gpu_shadows(64, 1);
}

static void s3d_gpu_atExit(void);

/* Starts the backend the first time it is asked for: 1 when it is there. */
static int s3d_gpu_ready(void) {
    if (g_s3dVk.state == 0) {
        g_s3dVk.state = s3d_gpu_start() ? 1 : -1;
        /* (registered after Vulkan was loaded: it runs before the driver's own clean-up at the end of the program, and before the windows close) */
        if (g_s3dVk.state == 1) atexit(s3d_gpu_atExit);
    }
    return g_s3dVk.state == 1;
}

/* ---- drawing ---- */
static void s3d_gpu_copyMat(float* out, const S3dMat4* m) { memcpy(out, m->m, sizeof(float) * 16); }

/* The world bounds of the vertices of a batch (the casters of shadows): the software renderer takes the same bounds from its transformed vertices. */
static void s3d_gpu_bounds(const S3dBatch* b, S3dVec3* lo, S3dVec3* hi) {
    size_t i, n = b->mesh->posN / 3;
    for (i = 0; i < n; i++) {
        S3dVec3 w = s3d_transformPoint(&b->model, s3d_meshPos(b->mesh, i));
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

static void s3d_gpu_viewportAt(int x, int y, uint32_t w, uint32_t h) {
    VkViewport vp;
    VkRect2D sc;
    vp.x = (float)x; vp.y = (float)y; vp.width = (float)w; vp.height = (float)h; vp.minDepth = 0; vp.maxDepth = 1;
    sc.offset.x = x; sc.offset.y = y; sc.extent.width = w; sc.extent.height = h;
    g_s3dVk.vkCmdSetViewport(g_s3dVk.cmd, 0, 1, &vp);
    g_s3dVk.vkCmdSetScissor(g_s3dVk.cmd, 0, 1, &sc);
}
static void s3d_gpu_viewport(uint32_t w, uint32_t h) { s3d_gpu_viewportAt(0, 0, w, h); }

/* Packs the shadow maps into an atlas of layers of `size` x `size`: the biggest first, in rows (shelves). Returns the number of layers, or -1 when that is more than `maxLayers`. */
static int s3d_gpu_pack(S3dGpuTile* tiles, size_t n, int size, int maxLayers) {
    size_t i, k;
    int layer = 0, x = 0, y = 0, rowH = 0, used = 0;
    /* the order: by size, the biggest first (stable) */
    for (i = 0; i < n; i++) tiles[i].order = (int)i;
    for (i = 1; i < n; i++) {
        int o = tiles[i].order;
        k = i;
        while (k > 0 && tiles[tiles[k - 1].order].res < tiles[o].res) { tiles[k].order = tiles[k - 1].order; k--; }
        tiles[k].order = o;
    }
    for (i = 0; i < n; i++) {
        S3dGpuTile* t = &tiles[tiles[i].order];
        if (x + t->res > size) { x = 0; y += rowH; rowH = 0; }
        if (y + t->res > size) { layer++; x = 0; y = 0; rowH = 0; }
        if (layer >= maxLayers) return -1;
        t->x = x; t->y = y; t->layer = layer;
        x += t->res;
        if (t->res > rowH) rowH = t->res;
        used = layer + 1;
    }
    return used;
}

/* Fills the uniforms of the picture (camera, lights) and plans the shadow maps: every map gets its place in the atlas. When the maps do not fit into the memory for them, all
 * of them are made smaller (half the size, again and again) - a light never loses its shadow for that. Returns the number of maps; *layersOut and *sizeOut: the atlas. */
static int s3d_gpu_frameUniforms(S3dFrame* f, const S3dCamera* cam, S3dVec3 lo, S3dVec3 hi, int* layersOut, int* sizeOut) {
    S3dGpuFrameU* u = &g_s3dVk.frameData;
    S3dContext* ctx = &f->ctx;
    /* the projection of the engine (OpenGL clip space) for Vulkan: y down, z from 0 to w */
    S3dMat4 vk = s3d_identity(), vp;
    size_t nMaps = 0, li, n = 0, i;
    int k, m, size = 64, layers = 1, shift;
    S3dGpuLightU* lightOut;
    int* firstMap;
    S3D_AT(vk, 1, 1) = -1; S3D_AT(vk, 2, 2) = 0.5f; S3D_AT(vk, 2, 3) = 0.5f;
    vp = s3d_mul(vk, s3d_viewProjection(cam, f->w, f->h));
    memset(u, 0, sizeof *u);
    s3d_gpu_copyMat(u->vp, &vp);
    u->eye[0] = ctx->eye.x; u->eye[1] = ctx->eye.y; u->eye[2] = ctx->eye.z;
    u->ambient[0] = ctx->ambient.x; u->ambient[1] = ctx->ambient.y; u->ambient[2] = ctx->ambient.z;
    for (k = 0; k < ctx->shadowN; k++) nMaps += (size_t)ctx->shadows[k].count;
    if (nMaps + 1 > g_s3dVk.mapCap) {
        S3dGpuMapU* md = (S3dGpuMapU*)realloc(g_s3dVk.mapData, sizeof(S3dGpuMapU) * (nMaps + 1));
        S3dGpuTile* td;
        if (md == NULL) return -1;
        g_s3dVk.mapData = md;
        td = (S3dGpuTile*)realloc(g_s3dVk.tiles, sizeof(S3dGpuTile) * (nMaps + 1));
        if (td == NULL) return -1;
        g_s3dVk.tiles = td;
        g_s3dVk.mapCap = nMaps + 1;
    }
    firstMap = (int*)malloc(sizeof(int) * ((size_t)ctx->shadowN + 1));
    if (firstMap == NULL) return -1;
    /* the maps of the shadow sets in order; the atlas is as big as the biggest map */
    nMaps = 0;
    for (k = 0; k < ctx->shadowN; k++) {
        firstMap[k] = (int)nMaps;
        for (m = 0; m < ctx->shadows[k].count; m++) {
            int res = s3d_mini(ctx->shadows[k].maps[m].res, (int)g_s3dVk.maxImageDim);
            g_s3dVk.tiles[nMaps++].res = res;
            size = s3d_maxi(size, res);
        }
    }
    for (shift = 0; nMaps > 0; shift++) {
        int maxLayers = (int)g_s3dVk.maxLayers;
        VkDeviceSize perLayer = (VkDeviceSize)size * (VkDeviceSize)size * 8;   /* (R32F, and the depth buffer once) */
        if ((VkDeviceSize)maxLayers * perLayer > g_s3dVk.shadowBudget) maxLayers = (int)(g_s3dVk.shadowBudget / perLayer);
        if (maxLayers < 1) maxLayers = 1;
        for (i = 0; i < nMaps; i++) g_s3dVk.tiles[i].res = s3d_maxi(16, g_s3dVk.tiles[i].res >> (shift > 0 ? 1 : 0));
        layers = s3d_gpu_pack(g_s3dVk.tiles, nMaps, size, maxLayers);
        if (layers > 0) break;
        if (shift > 24) { for (i = 0; i < nMaps; i++) g_s3dVk.tiles[i].layer = -1; layers = 1; break; }
    }
    /* the maps */
    nMaps = 0;
    for (k = 0; k < ctx->shadowN; k++) {
        const S3dShadowSet* set = &ctx->shadows[k];
        for (m = 0; m < set->count; m++, nMaps++) {
            const S3dShadowMap* sm = &set->maps[m];
            S3dGpuMapU* g = &g_s3dVk.mapData[nMaps];
            const S3dGpuTile* t = &g_s3dVk.tiles[nMaps];
            float dmin = 1e30f, dmax = -1e30f, farthest = 1.0f;
            int c;
            memset(g, 0, sizeof *g);
            s3d_gpu_copyMat(g->view, &sm->view);
            g->p[0] = sm->sx; g->p[1] = sm->sy; g->p[2] = sm->ox; g->p[3] = sm->oy;
            g->q[0] = sm->f; g->q[1] = (float)t->res; g->q[2] = sm->ortho ? 1.0f : 0.0f;
            g->t[0] = t->x; g->t[1] = t->y; g->t[2] = t->layer;
            /* the depth range of the casters in the view of the light (orthographic) or how far they are (perspective) */
            for (c = 0; c < 8; c++) {
                S3dVec3 corner = s3d_v3((c & 1) ? hi.x : lo.x, (c & 2) ? hi.y : lo.y, (c & 4) ? hi.z : lo.z);
                S3dVec3 q = s3d_transformPoint(&sm->view, corner);
                dmin = s3d_minf(dmin, -q.z); dmax = s3d_maxf(dmax, -q.z);
                farthest = s3d_maxf(farthest, s3d_length(q));
            }
            dmin -= 0.01f + 0.01f * (dmax - dmin); dmax += 0.01f + 0.01f * (dmax - dmin);
            g->r[0] = dmin; g->r[1] = 1.0f / s3d_maxf(dmax - dmin, 1e-6f); g->r[2] = farthest * 1.5f + 1.0f;
        }
    }
    /* the lights */
    if (!s3d_gpu_hostBuffer(&g_s3dVk.lightBuf, sizeof(S3dGpuLightU) * (ctx->lightN + 1), VK_BUFFER_USAGE_STORAGE_BUFFER_BIT)
        || !s3d_gpu_hostBuffer(&g_s3dVk.mapBuf, sizeof(S3dGpuMapU) * (nMaps + 1), VK_BUFFER_USAGE_STORAGE_BUFFER_BIT)) { free(firstMap); return -1; }
    lightOut = (S3dGpuLightU*)g_s3dVk.lightBuf.map;
    for (li = 0; li < ctx->lightN; li++) {
        const S3dPreparedLight* L = &ctx->lights[li];
        S3dGpuLightU* g = &lightOut[n++];
        memset(g, 0, sizeof *g);
        g->info[0] = L->kind; g->info[1] = L->mask;
        g->info[2] = L->shadow >= 0 && L->shadow < ctx->shadowN && g_s3dVk.tiles[firstMap[L->shadow]].layer >= 0 ? firstMap[L->shadow] : -1;
        g->color[0] = L->color.x; g->color[1] = L->color.y; g->color[2] = L->color.z;
        g->pos[0] = L->pos.x; g->pos[1] = L->pos.y; g->pos[2] = L->pos.z; g->pos[3] = L->invRange;
        g->toLight[0] = L->toLight.x; g->toLight[1] = L->toLight.y; g->toLight[2] = L->toLight.z;
    }
    free(firstMap);
    u->counts[0] = (int32_t)n;
    memcpy(g_s3dVk.frameUbo.map, u, sizeof *u);
    memcpy(g_s3dVk.mapBuf.map, g_s3dVk.mapData, sizeof(S3dGpuMapU) * nMaps);
    *layersOut = layers;
    *sizeOut = size;
    return (int)nMaps;
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

/* Draws the queue: 1 when the picture is in `rgba`, 2 when it stays on the GPU for the windows of the camera (DirectDrawing), 0 when the GPU cannot draw it. */
static int s3d_gpu_flushLocked(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st) {
    S3dGpuFrame* target;
    S3dGpuTarget* direct = (S3dGpuTarget*)f->direct;
    int readBack = !(direct != NULL && direct->attached && direct->works == 1 && g_s3dVk.wsi);
    size_t i, nb = f->batchN, k;
    S3dVec3 lo = s3d_v3(1e30f, 1e30f, 1e30f), hi = s3d_v3(-1e30f, -1e30f, -1e30f);
    int anyCaster = 0, maps = 0, atlas = 64, layers = 1, m, nblended = 0, layer;
    VkRenderPassBeginInfo rp;
    VkClearValue clear[2];
    VkBufferImageCopy region;
    VkMemoryBarrier mb;
    if (!s3d_gpu_ready()) return 0;
    if ((uint32_t)f->w > g_s3dVk.maxImageDim || (uint32_t)f->h > g_s3dVk.maxImageDim) return 0;
    if (!s3d_gpu_waitPending()) { g_s3dVk.state = -1; return 0; }   /* (the previous picture: its buffers are written again now) */
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
    maps = s3d_gpu_frameUniforms(f, cam, lo, hi, &layers, &atlas);
    if (maps < 0 || !s3d_gpu_shadows(atlas, layers)) return 0;
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
    /* the shadow maps: a render pass per layer of the atlas, every map in its place */
    if (maps > 0 && s3d_gpu_pipeline(1, 0, 0, 1)) {
        for (layer = 0; layer < layers; layer++) {
            uint32_t dynamicZero = 0;
            memset(&rp, 0, sizeof rp);
            rp.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
            rp.renderPass = g_s3dVk.passShadow;
            rp.framebuffer = g_s3dVk.layerFbs[layer];
            rp.renderArea.extent.width = (uint32_t)atlas; rp.renderArea.extent.height = (uint32_t)atlas;
            memset(clear, 0, sizeof clear);
            clear[0].color.float32[0] = 1e30f;
            clear[1].depthStencil.depth = 1.0f;
            rp.clearValueCount = 2; rp.pClearValues = clear;
            g_s3dVk.vkCmdBeginRenderPass(g_s3dVk.cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);
            g_s3dVk.vkCmdBindPipeline(g_s3dVk.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_s3dVk.shadowPipe);
            g_s3dVk.vkCmdBindDescriptorSets(g_s3dVk.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_s3dVk.layoutShadow, 0, 1, &g_s3dVk.frameSet, 1, &dynamicZero);
            for (m = 0; m < maps; m++) {
                const S3dGpuTile* t = &g_s3dVk.tiles[m];
                if (t->layer != layer) continue;
                s3d_gpu_viewportAt(t->x, t->y, (uint32_t)t->res, (uint32_t)t->res);
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
    target->hasPicture = 1;
    target->colorLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    if (!readBack) {
        /* DirectDrawing: the picture stays on the GPU; the windows show it when they present (the next flush waits for this one) */
        if (!s3d_gpu_submit(0)) { g_s3dVk.state = -1; return 0; }
        return 2;
    }
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
static int s3d_gpu_flush(uint8_t* rgba, S3dFrame* f, const S3dCamera* cam, const S3dLight* lights, s3d_size lightCount, const S3dFlushSettings* st) {
    int r;
    s3d_gpu_lock();
    r = s3d_gpu_flushLocked(rgba, f, cam, lights, lightCount, st);
    s3d_gpu_unlock();
    return r;
}

/* ---------------------------------------------------------------------------------------------------------------------------------------------
 * DirectDrawing: the presenter of a camera, called by the windows that show its framebuffer (on their thread)
 * --------------------------------------------------------------------------------------------------------------------------------------------- */
static void s3d_gpu_barrierOn(VkCommandBuffer cmd, VkImage img, VkImageLayout from, VkImageLayout to, VkAccessFlags srcAccess, VkAccessFlags dstAccess,
                              VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage) {
    VkImageMemoryBarrier b;
    memset(&b, 0, sizeof b);
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = srcAccess; b.dstAccessMask = dstAccess;
    b.oldLayout = from; b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1; b.subresourceRange.layerCount = 1;
    g_s3dVk.vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, NULL, 0, NULL, 1, &b);
}

/* The two images of the present pass (the camera's picture, the overlay) into a set, with the sampler that clamps. */
static void s3d_gpu_setImages(VkDescriptorSet set, VkImageView scene, VkImageView overlay) {
    VkDescriptorImageInfo info[2];
    VkWriteDescriptorSet w[2];
    int i;
    info[0].sampler = g_s3dVk.clamp; info[0].imageView = scene; info[0].imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    info[1].sampler = g_s3dVk.clamp; info[1].imageView = overlay; info[1].imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    memset(w, 0, sizeof w);
    for (i = 0; i < 2; i++) {
        w[i].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET; w[i].dstSet = set; w[i].dstBinding = (uint32_t)i; w[i].descriptorCount = 1;
        w[i].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; w[i].pImageInfo = &info[i];
    }
    g_s3dVk.vkUpdateDescriptorSets(g_s3dVk.device, 2, w, 0, NULL);
}

/* What every presenter shares: its shaders, the layout (the two images and the flags), the sampler. */
static int s3d_gpu_presentShared(void) {
    VkPipelineLayoutCreateInfo pi;
    VkPushConstantRange pc;
    VkSamplerCreateInfo sc;
    if (g_s3dVk.layoutPresent) return 1;
    if (!s3d_gpu_shader(s3d_spv_present_vert, sizeof s3d_spv_present_vert, &g_s3dVk.presentVert)
        || !s3d_gpu_shader(s3d_spv_present_frag, sizeof s3d_spv_present_frag, &g_s3dVk.presentFrag)) return 0;
    memset(&sc, 0, sizeof sc);
    sc.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sc.magFilter = VK_FILTER_LINEAR; sc.minFilter = VK_FILTER_LINEAR;
    sc.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    sc.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE; sc.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE; sc.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sc.borderColor = VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE;
    if (!s3d_gpu_check(g_s3dVk.vkCreateSampler(g_s3dVk.device, &sc, NULL, &g_s3dVk.clamp), "vkCreateSampler")) return 0;
    memset(&pc, 0, sizeof pc);
    pc.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    pc.size = 16;
    memset(&pi, 0, sizeof pi);
    pi.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pi.setLayoutCount = 1; pi.pSetLayouts = &g_s3dVk.setTex;
    pi.pushConstantRangeCount = 1; pi.pPushConstantRanges = &pc;
    return s3d_gpu_check(g_s3dVk.vkCreatePipelineLayout(g_s3dVk.device, &pi, NULL, &g_s3dVk.layoutPresent), "vkCreatePipelineLayout");
}

/* The render pass into the images of a swap chain (format `format`) and the pipeline of the present pass with it. */
static int s3d_gpu_presentPass(S3dGpuTarget* t, VkFormat format) {
    VkAttachmentDescription att;
    VkAttachmentReference ref;
    VkSubpassDescription sub;
    VkSubpassDependency dep;
    VkRenderPassCreateInfo rc;
    VkPipelineShaderStageCreateInfo stages[2];
    VkPipelineVertexInputStateCreateInfo vi;
    VkPipelineInputAssemblyStateCreateInfo ia;
    VkPipelineViewportStateCreateInfo vp;
    VkPipelineRasterizationStateCreateInfo rs;
    VkPipelineMultisampleStateCreateInfo ms;
    VkPipelineColorBlendAttachmentState cba;
    VkPipelineColorBlendStateCreateInfo cb;
    VkDynamicState dyn[2];
    VkPipelineDynamicStateCreateInfo dy;
    VkGraphicsPipelineCreateInfo ci;
    if (t->pass && t->format == format) return 1;
    if (t->pipe) { g_s3dVk.vkDestroyPipeline(g_s3dVk.device, t->pipe, NULL); t->pipe = 0; }
    if (t->pass) { g_s3dVk.vkDestroyRenderPass(g_s3dVk.device, t->pass, NULL); t->pass = 0; }
    memset(&att, 0, sizeof att);
    att.format = format;
    att.samples = VK_SAMPLE_COUNT_1_BIT;
    att.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR; att.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    att.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE; att.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED; att.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    ref.attachment = 0; ref.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    memset(&sub, 0, sizeof sub);
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1; sub.pColorAttachments = &ref;
    /* the image is written once the swap chain has given it (the semaphore of the acquire is waited for at this stage) */
    memset(&dep, 0, sizeof dep);
    dep.srcSubpass = VK_SUBPASS_EXTERNAL; dep.dstSubpass = 0;
    dep.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT; dep.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep.srcAccessMask = 0; dep.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    memset(&rc, 0, sizeof rc);
    rc.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
    rc.attachmentCount = 1; rc.pAttachments = &att;
    rc.subpassCount = 1; rc.pSubpasses = &sub;
    rc.dependencyCount = 1; rc.pDependencies = &dep;
    if (!s3d_gpu_check(g_s3dVk.vkCreateRenderPass(g_s3dVk.device, &rc, NULL, &t->pass), "vkCreateRenderPass")) { t->pass = 0; return 0; }
    t->format = format;
    memset(stages, 0, sizeof stages);
    stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO; stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT; stages[0].module = g_s3dVk.presentVert; stages[0].pName = "main";
    stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO; stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].module = g_s3dVk.presentFrag; stages[1].pName = "main";
    memset(&vi, 0, sizeof vi); vi.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    memset(&ia, 0, sizeof ia); ia.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO; ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    memset(&vp, 0, sizeof vp); vp.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO; vp.viewportCount = 1; vp.scissorCount = 1;
    memset(&rs, 0, sizeof rs); rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rs.polygonMode = VK_POLYGON_MODE_FILL; rs.cullMode = VK_CULL_MODE_NONE; rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE; rs.lineWidth = 1.0f;
    memset(&ms, 0, sizeof ms); ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO; ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    memset(&cba, 0, sizeof cba);
    cba.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    memset(&cb, 0, sizeof cb); cb.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO; cb.attachmentCount = 1; cb.pAttachments = &cba;
    dyn[0] = VK_DYNAMIC_STATE_VIEWPORT; dyn[1] = VK_DYNAMIC_STATE_SCISSOR;
    memset(&dy, 0, sizeof dy); dy.sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO; dy.dynamicStateCount = 2; dy.pDynamicStates = dyn;
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    ci.stageCount = 2; ci.pStages = stages;
    ci.pVertexInputState = &vi; ci.pInputAssemblyState = &ia; ci.pViewportState = &vp; ci.pRasterizationState = &rs;
    ci.pMultisampleState = &ms; ci.pColorBlendState = &cb; ci.pDynamicState = &dy;
    ci.layout = g_s3dVk.layoutPresent;
    ci.renderPass = t->pass;
    if (!s3d_gpu_check(g_s3dVk.vkCreateGraphicsPipelines(g_s3dVk.device, 0, 1, &ci, NULL, &t->pipe), "vkCreateGraphicsPipelines")) { t->pipe = 0; return 0; }
    return 1;
}

static void s3d_gpu_freeSwapchainImages(S3dGpuTarget* t) {
    uint32_t i;
    for (i = 0; i < t->imageCount; i++) {
        if (t->fbs && t->fbs[i]) g_s3dVk.vkDestroyFramebuffer(g_s3dVk.device, t->fbs[i], NULL);
        if (t->views && t->views[i]) g_s3dVk.vkDestroyImageView(g_s3dVk.device, t->views[i], NULL);
        if (t->rendered && t->rendered[i]) g_s3dVk.vkDestroySemaphore(g_s3dVk.device, t->rendered[i], NULL);
    }
    free(t->images); free(t->views); free(t->fbs); free(t->rendered);
    t->images = NULL; t->views = NULL; t->fbs = NULL; t->rendered = NULL;
    t->imageCount = 0;
}

/* Lets go of the window: the swap chain and the surface (before the window closes, or shows its framebuffer again). */
static void s3d_gpu_freeSurface(S3dGpuTarget* t) {
    if (!t->surface && !t->swapchain) return;
    s3d_gpu_idle();
    t->fencePending = 0;
    s3d_gpu_freeSwapchainImages(t);
    if (t->swapchain) { g_s3dVk.vkDestroySwapchainKHR(g_s3dVk.device, t->swapchain, NULL); t->swapchain = 0; }
    if (t->surface) { g_s3dVk.vkDestroySurfaceKHR(g_s3dVk.instance, t->surface, NULL); t->surface = 0; }
    memset(&t->win, 0, sizeof t->win);
}

static int s3d_gpu_sameWindow(const fire_native_window* a, const fire_native_window* b) {
    return a->kind == b->kind && a->a == b->a && a->b == b->b && a->id == b->id;
}

/* A surface for the window (Win32, X11, Wayland, a CAMetalLayer). */
static int s3d_gpu_makeSurface(S3dGpuTarget* t, const fire_native_window* w) {
    VkResult r = VK_ERROR_EXTENSION_NOT_PRESENT;
    VkBool32 supported = VK_FALSE;
    switch (w->kind) {
        case FIRE_NATIVE_WIN32: if (g_s3dVk.createWin32Surface) {
            S3dVkWin32SurfaceCreateInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = S3D_VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR; ci.hinstance = w->a; ci.hwnd = w->b;
            r = g_s3dVk.createWin32Surface(g_s3dVk.instance, &ci, NULL, &t->surface);
        } break;
        case FIRE_NATIVE_X11: if (g_s3dVk.createXlibSurface) {
            S3dVkXlibSurfaceCreateInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = S3D_VK_STRUCTURE_TYPE_XLIB_SURFACE_CREATE_INFO_KHR; ci.dpy = w->a; ci.window = (unsigned long)w->id;
            r = g_s3dVk.createXlibSurface(g_s3dVk.instance, &ci, NULL, &t->surface);
        } break;
        case FIRE_NATIVE_WAYLAND: if (g_s3dVk.createWaylandSurface) {
            S3dVkWaylandSurfaceCreateInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = S3D_VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR; ci.display = w->a; ci.surface = w->b;
            r = g_s3dVk.createWaylandSurface(g_s3dVk.instance, &ci, NULL, &t->surface);
        } break;
        case FIRE_NATIVE_METAL: if (g_s3dVk.createMetalSurface) {
            S3dVkMetalSurfaceCreateInfo ci; memset(&ci, 0, sizeof ci);
            ci.sType = S3D_VK_STRUCTURE_TYPE_METAL_SURFACE_CREATE_INFO_EXT; ci.pLayer = w->b;
            r = g_s3dVk.createMetalSurface(g_s3dVk.instance, &ci, NULL, &t->surface);
        } break;
        default: break;
    }
    if (r != VK_SUCCESS) { t->surface = 0; s3d_gpu_fail("The GPU cannot draw into windows of this window system."); return 0; }
    if (g_s3dVk.vkGetPhysicalDeviceSurfaceSupportKHR(g_s3dVk.phys, g_s3dVk.family, t->surface, &supported) != VK_SUCCESS || !supported) {
        g_s3dVk.vkDestroySurfaceKHR(g_s3dVk.instance, t->surface, NULL);
        t->surface = 0;
        s3d_gpu_fail("The GPU cannot show pictures in this window.");
        return 0;
    }
    t->win = *w;
    return 1;
}

/* The swap chain of the window, as big as the window (made again when it changes): 1 ready, 2 the window has no size (minimized), 0 an error. */
static int s3d_gpu_makeSwapchain(S3dGpuTarget* t, const fire_native_window* w) {
    VkSurfaceCapabilitiesKHR caps;
    VkSurfaceFormatKHR* formats = NULL;
    VkPresentModeKHR modes[16];
    uint32_t nf = 0, nm = 16, i, count = 0;
    VkFormat format;
    VkPresentModeKHR mode = VK_PRESENT_MODE_FIFO_KHR;
    VkExtent2D extent;
    VkSwapchainCreateInfoKHR ci;
    VkSwapchainKHR old = t->swapchain;
    VkColorSpaceKHR space;
    if (!s3d_gpu_check(g_s3dVk.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(g_s3dVk.phys, t->surface, &caps), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR")) return 0;
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
    if (t->swapchain && !t->stale && extent.width == t->extent.width && extent.height == t->extent.height && (w->vsync != 0) == (t->vsync != 0)) return 1;
    /* the colours as they are (UNORM, not sRGB: a window shows the bytes of a framebuffer as they are) */
    if (g_s3dVk.vkGetPhysicalDeviceSurfaceFormatsKHR(g_s3dVk.phys, t->surface, &nf, NULL) != VK_SUCCESS || nf == 0) { s3d_gpu_fail("The window has no picture formats."); return 0; }
    formats = (VkSurfaceFormatKHR*)malloc(sizeof(VkSurfaceFormatKHR) * nf);
    if (formats == NULL || g_s3dVk.vkGetPhysicalDeviceSurfaceFormatsKHR(g_s3dVk.phys, t->surface, &nf, formats) != VK_SUCCESS) { free(formats); return 0; }
    format = formats[0].format == VK_FORMAT_UNDEFINED ? VK_FORMAT_B8G8R8A8_UNORM : formats[0].format;
    space = formats[0].colorSpace;
    for (i = 0; i < nf; i++)
        if (formats[i].format == VK_FORMAT_B8G8R8A8_UNORM || formats[i].format == VK_FORMAT_R8G8B8A8_UNORM) { format = formats[i].format; space = formats[i].colorSpace; break; }
    free(formats);
    /* VSync: FIFO (every driver has it); without: MAILBOX (no tearing) or IMMEDIATE */
    if (!w->vsync && g_s3dVk.vkGetPhysicalDeviceSurfacePresentModesKHR(g_s3dVk.phys, t->surface, &nm, modes) >= 0) {
        for (i = 0; i < nm; i++) if (modes[i] == VK_PRESENT_MODE_MAILBOX_KHR) mode = VK_PRESENT_MODE_MAILBOX_KHR;
        if (mode != VK_PRESENT_MODE_MAILBOX_KHR) for (i = 0; i < nm; i++) if (modes[i] == VK_PRESENT_MODE_IMMEDIATE_KHR) mode = VK_PRESENT_MODE_IMMEDIATE_KHR;
    }
    if (!s3d_gpu_presentPass(t, format)) return 0;
    s3d_gpu_idle();
    t->fencePending = 0;
    s3d_gpu_freeSwapchainImages(t);
    memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
    ci.surface = t->surface;
    ci.minImageCount = caps.minImageCount + 1;
    if (caps.maxImageCount > 0 && ci.minImageCount > caps.maxImageCount) ci.minImageCount = caps.maxImageCount;
    ci.imageFormat = format;
    ci.imageColorSpace = space;
    ci.imageExtent = extent;
    ci.imageArrayLayers = 1;
    ci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.preTransform = caps.currentTransform;
    ci.compositeAlpha = (caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR) ? VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR
                      : (caps.supportedCompositeAlpha & VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR) ? VK_COMPOSITE_ALPHA_INHERIT_BIT_KHR : VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR;
    ci.presentMode = mode;
    ci.clipped = VK_TRUE;
    ci.oldSwapchain = old;
    if (!s3d_gpu_check(g_s3dVk.vkCreateSwapchainKHR(g_s3dVk.device, &ci, NULL, &t->swapchain), "vkCreateSwapchainKHR")) {
        t->swapchain = 0;
        if (old) g_s3dVk.vkDestroySwapchainKHR(g_s3dVk.device, old, NULL);
        return 0;
    }
    if (old) g_s3dVk.vkDestroySwapchainKHR(g_s3dVk.device, old, NULL);
    t->extent = extent; t->vsync = w->vsync != 0; t->stale = 0;
    if (g_s3dVk.vkGetSwapchainImagesKHR(g_s3dVk.device, t->swapchain, &count, NULL) != VK_SUCCESS || count == 0) return 0;
    t->images = (VkImage*)calloc(count, sizeof(VkImage));
    t->views = (VkImageView*)calloc(count, sizeof(VkImageView));
    t->fbs = (VkFramebuffer*)calloc(count, sizeof(VkFramebuffer));
    t->rendered = (VkSemaphore*)calloc(count, sizeof(VkSemaphore));
    if (!t->images || !t->views || !t->fbs || !t->rendered) { t->imageCount = count; return 0; }
    t->imageCount = count;
    if (g_s3dVk.vkGetSwapchainImagesKHR(g_s3dVk.device, t->swapchain, &count, t->images) != VK_SUCCESS) return 0;
    for (i = 0; i < count; i++) {
        VkImageViewCreateInfo vi;
        VkFramebufferCreateInfo fi;
        VkSemaphoreCreateInfo si;
        memset(&vi, 0, sizeof vi);
        vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
        vi.image = t->images[i]; vi.viewType = VK_IMAGE_VIEW_TYPE_2D; vi.format = format;
        vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; vi.subresourceRange.levelCount = 1; vi.subresourceRange.layerCount = 1;
        if (!s3d_gpu_check(g_s3dVk.vkCreateImageView(g_s3dVk.device, &vi, NULL, &t->views[i]), "vkCreateImageView")) { t->views[i] = 0; return 0; }
        memset(&fi, 0, sizeof fi);
        fi.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
        fi.renderPass = t->pass; fi.attachmentCount = 1; fi.pAttachments = &t->views[i];
        fi.width = extent.width; fi.height = extent.height; fi.layers = 1;
        if (!s3d_gpu_check(g_s3dVk.vkCreateFramebuffer(g_s3dVk.device, &fi, NULL, &t->fbs[i]), "vkCreateFramebuffer")) { t->fbs[i] = 0; return 0; }
        memset(&si, 0, sizeof si);
        si.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
        if (!s3d_gpu_check(g_s3dVk.vkCreateSemaphore(g_s3dVk.device, &si, NULL, &t->rendered[i]), "vkCreateSemaphore")) { t->rendered[i] = 0; return 0; }
    }
    return 1;
}

/* The overlay: the framebuffer's pixels, where the program drew something (alpha not 0). 1: there is one (uploaded into t->overlay by the commands), 0: none, -1: an error. */
static int s3d_gpu_overlay(S3dGpuTarget* t, const uint32_t* pixels, int width, int height) {
    size_t n = (size_t)width * (size_t)height, i;
    int any = 0;
    if (pixels == NULL || width <= 0 || height <= 0) return 0;
    for (i = 0; i < n; i++) if (pixels[i] >> 24) { any = 1; break; }   /* (R,G,B,A in memory: the alpha is the top byte of the word on little endian) */
    if (!any) { const uint8_t* b = (const uint8_t*)pixels; for (i = 0; i < n; i++) if (b[i * 4 + 3]) { any = 1; break; } }
    if (!any) return 0;
    if (!t->overlay.img || t->overlayW != width || t->overlayH != height) {
        s3d_gpu_idle();
        s3d_gpu_freeImage(&t->overlay);
        if (!s3d_gpu_image(&t->overlay, (uint32_t)width, (uint32_t)height, 1, VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT, VK_IMAGE_ASPECT_COLOR_BIT)) return -1;
        t->overlayW = width; t->overlayH = height;
    }
    if (!s3d_gpu_hostBuffer(&t->staging, (VkDeviceSize)n * 4, VK_BUFFER_USAGE_TRANSFER_SRC_BIT)) return -1;
    memcpy(t->staging.map, pixels, n * 4);
    return 1;
}

/* Shows the camera's picture with the overlay in the window (the lock is held). 1: shown (or nothing to show: a minimized window), 0: it cannot. */
static int s3d_gpu_presentLocked(S3dGpuTarget* t, const fire_native_window* w, const uint32_t* pixels, int width, int height) {
    S3dGpuFrame* g = t->frame ? (S3dGpuFrame*)t->frame->gpu : NULL;
    int hasScene = g != NULL && g->hasPicture, hasOverlay, made, attempt;
    uint32_t index = 0, flags[4];
    VkResult r = VK_SUCCESS;
    VkCommandBufferBeginInfo bi;
    VkRenderPassBeginInfo rp;
    VkClearValue clear;
    VkViewport vp;
    VkRect2D sc;
    VkSubmitInfo si;
    VkPresentInfoKHR pi;
    VkPipelineStageFlags waitStage = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    if (!s3d_gpu_presentShared()) return 0;
    if (t->surface && !s3d_gpu_sameWindow(&t->win, w)) {
        /* another window shows the same framebuffer: one window per camera - that one shows its framebuffer as before */
        return 0;
    }
    if (!t->surface && !s3d_gpu_makeSurface(t, w)) return 0;
    if (t->fencePending) {
        if (!s3d_gpu_check(g_s3dVk.vkWaitForFences(g_s3dVk.device, 1, &t->fence, VK_TRUE, UINT64_MAX), "vkWaitForFences")) return 0;
        t->fencePending = 0;
    }
    for (attempt = 0; ; attempt++) {
        made = s3d_gpu_makeSwapchain(t, w);
        if (made == 0) return 0;
        if (made == 2) return 1;   /* (minimized: nothing to show) */
        r = g_s3dVk.vkAcquireNextImageKHR(g_s3dVk.device, t->swapchain, UINT64_MAX, t->acquired, 0, &index);
        if (r == VK_ERROR_OUT_OF_DATE_KHR && attempt == 0) { t->stale = 1; continue; }
        break;
    }
    if (r == VK_SUBOPTIMAL_KHR) t->stale = 1;
    else if (r != VK_SUCCESS) { s3d_gpu_check(r, "vkAcquireNextImageKHR"); return 0; }
    hasOverlay = s3d_gpu_overlay(t, pixels, width, height);
    if (hasOverlay < 0) return 0;
    s3d_gpu_setImages(t->set, hasScene ? g->color.view : g_s3dVk.dummy.color.view, hasOverlay ? t->overlay.view : g_s3dVk.dummy.color.view);
    /* the commands */
    if (!s3d_gpu_check(g_s3dVk.vkResetCommandBuffer(t->cmd, 0), "vkResetCommandBuffer")) return 0;
    memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (!s3d_gpu_check(g_s3dVk.vkBeginCommandBuffer(t->cmd, &bi), "vkBeginCommandBuffer")) return 0;
    if (hasOverlay) {
        VkBufferImageCopy region;
        /* (after the previous picture read it) */
        s3d_gpu_barrierOn(t->cmd, t->overlay.img, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
                          VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
        memset(&region, 0, sizeof region);
        region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; region.imageSubresource.layerCount = 1;
        region.imageExtent.width = (uint32_t)width; region.imageExtent.height = (uint32_t)height; region.imageExtent.depth = 1;
        g_s3dVk.vkCmdCopyBufferToImage(t->cmd, t->staging.buf, t->overlay.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
        s3d_gpu_barrierOn(t->cmd, t->overlay.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT,
                          VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
    }
    if (hasScene && g->colorLayout != VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL) {
        /* the camera's picture (drawn by a flush before, on the same queue) for the shader */
        s3d_gpu_barrierOn(t->cmd, g->color.img, g->colorLayout, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT | VK_ACCESS_TRANSFER_READ_BIT,
                          VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT | VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
        g->colorLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    }
    memset(&rp, 0, sizeof rp);
    memset(&clear, 0, sizeof clear);
    clear.color.float32[3] = 1.0f;
    rp.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
    rp.renderPass = t->pass; rp.framebuffer = t->fbs[index];
    rp.renderArea.extent = t->extent;
    rp.clearValueCount = 1; rp.pClearValues = &clear;
    g_s3dVk.vkCmdBeginRenderPass(t->cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);
    vp.x = 0; vp.y = 0; vp.width = (float)t->extent.width; vp.height = (float)t->extent.height; vp.minDepth = 0; vp.maxDepth = 1;
    sc.offset.x = 0; sc.offset.y = 0; sc.extent = t->extent;
    g_s3dVk.vkCmdSetViewport(t->cmd, 0, 1, &vp);
    g_s3dVk.vkCmdSetScissor(t->cmd, 0, 1, &sc);
    g_s3dVk.vkCmdBindPipeline(t->cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, t->pipe);
    g_s3dVk.vkCmdBindDescriptorSets(t->cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_s3dVk.layoutPresent, 0, 1, &t->set, 0, NULL);
    flags[0] = (uint32_t)hasScene; flags[1] = (uint32_t)hasOverlay; flags[2] = 0; flags[3] = 0;
    g_s3dVk.vkCmdPushConstants(t->cmd, g_s3dVk.layoutPresent, VK_SHADER_STAGE_FRAGMENT_BIT, 0, 16, flags);
    g_s3dVk.vkCmdDraw(t->cmd, 3, 1, 0, 0);
    g_s3dVk.vkCmdEndRenderPass(t->cmd);
    if (!s3d_gpu_check(g_s3dVk.vkEndCommandBuffer(t->cmd), "vkEndCommandBuffer")) return 0;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = 1; si.pWaitSemaphores = &t->acquired; si.pWaitDstStageMask = &waitStage;
    si.commandBufferCount = 1; si.pCommandBuffers = &t->cmd;
    si.signalSemaphoreCount = 1; si.pSignalSemaphores = &t->rendered[index];
    if (!s3d_gpu_check(g_s3dVk.vkResetFences(g_s3dVk.device, 1, &t->fence), "vkResetFences")) return 0;
    if (!s3d_gpu_check(g_s3dVk.vkQueueSubmit(g_s3dVk.queue, 1, &si, t->fence), "vkQueueSubmit")) return 0;
    t->fencePending = 1;
    memset(&pi, 0, sizeof pi);
    pi.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    pi.waitSemaphoreCount = 1; pi.pWaitSemaphores = &t->rendered[index];
    pi.swapchainCount = 1; pi.pSwapchains = &t->swapchain; pi.pImageIndices = &index;
    r = g_s3dVk.vkQueuePresentKHR(g_s3dVk.queue, &pi);
    if (r == VK_ERROR_OUT_OF_DATE_KHR || r == VK_SUBOPTIMAL_KHR) t->stale = 1;   /* (made again at the next picture) */
    else if (r != VK_SUCCESS) { s3d_gpu_check(r, "vkQueuePresentKHR"); return 0; }
    return 1;
}

/* The presenter (fire_present_fn): called by a window that shows the camera's framebuffer, on the window's thread. */
static int32_t s3d_gpu_present(void* ctx, int32_t op, const fire_native_window* w, const uint32_t* pixels, int32_t width, int32_t height) {
    S3dGpuTarget* t = (S3dGpuTarget*)ctx;
    int ok = 0;
    s3d_gpu_lock();
    if (op == 0) {
        /* the window lets go (it closes, or shows its framebuffer again): only the window of the surface counts */
        if (w == NULL || !t->surface || s3d_gpu_sameWindow(&t->win, w)) {
            if (g_s3dVk.state == 1) s3d_gpu_freeSurface(t);
            if (t->works == 1) t->works = 0;   /* (the camera reads its pictures back again until a window takes them) */
        }
        s3d_gpu_unlock();
        return 1;
    }
    if (t->attached && g_s3dVk.state == 1 && g_s3dVk.wsi && w != NULL) {
        ok = s3d_gpu_presentLocked(t, w, pixels, width, height);
        if (ok) t->works = 1;
        else if (!t->surface || s3d_gpu_sameWindow(&t->win, w)) { s3d_gpu_freeSurface(t); t->works = -1; }
    }
    s3d_gpu_unlock();
    return ok;
}

/* The camera's picture goes into the windows of the framebuffer `framebuffer` (an id of the graphics bridge). 1: attached (the windows may still refuse: then the camera reads
 * its pictures back, as without), 0: there is no GPU, no swap chain, or no such framebuffer. */
static int s3d_gpu_register(int64_t framebuffer, S3dGpuTarget* t) {
#if defined(FIRE_LIBRARY)
    const fire_host* h = libraryHost();
    if (h == NULL || (size_t)h->size < offsetof(fire_host, win_detach) + sizeof(h->win_detach) || h->win_attach == NULL) return 0;
    return h->win_attach((int32_t)framebuffer, s3d_gpu_present, t) != 0;
#elif defined(FIRE_BRIDGE_GRAPHICS_H)
    return fire_gfx_attachPresenter(framebuffer, (void (*)(void))s3d_gpu_present, t);
#else
    (void)framebuffer; (void)t;
    return 0;
#endif
}
static void s3d_gpu_unregister(int64_t framebuffer, S3dGpuTarget* t) {
#if defined(FIRE_LIBRARY)
    const fire_host* h = libraryHost();
    if (h == NULL || (size_t)h->size < offsetof(fire_host, win_detach) + sizeof(h->win_detach) || h->win_detach == NULL) return;
    h->win_detach((int32_t)framebuffer, t);
#elif defined(FIRE_BRIDGE_GRAPHICS_H)
    fire_gfx_detachPresenter(framebuffer, t);
#else
    (void)framebuffer; (void)t;
#endif
}

static S3dGpuTarget* s3d_gpu_detachLocked(S3dFrame* f) {
    S3dGpuTarget* t = (S3dGpuTarget*)f->direct;
    if (t == NULL) return NULL;
    t->attached = 0;
    t->frame = NULL;
    f->direct = NULL;
    return t;   /* (its surface goes when its window lets go: the window finds the presenter gone at its next picture, or closes) */
}

static void s3d_gpu_detach(S3dFrame* f) {
    S3dGpuTarget* t;
    s3d_gpu_lock();
    t = s3d_gpu_detachLocked(f);
    s3d_gpu_unlock();
    if (t) s3d_gpu_unregister(t->framebuffer, t);
}

static int s3d_gpu_attach(S3dFrame* f, int64_t framebuffer) {
    S3dGpuTarget* t;
    VkCommandBufferAllocateInfo ai;
    VkFenceCreateInfo fi;
    VkSemaphoreCreateInfo si;
    if (f->direct && ((S3dGpuTarget*)f->direct)->framebuffer == framebuffer) return 1;
    if (f->direct) s3d_gpu_detach(f);
    s3d_gpu_lock();
    if (!s3d_gpu_ready() || !g_s3dVk.wsi || !s3d_gpu_presentShared()) { s3d_gpu_unlock(); return 0; }
    /* the presenter this framebuffer had before (switched off and on again): its window may still hold it, and it is the same window */
    for (t = g_s3dVk.targets; t != NULL; t = t->next)
        if (!t->attached && t->framebuffer == framebuffer) {
            t->attached = 1; t->frame = f; t->works = 0;
            f->direct = t;
            s3d_gpu_unlock();
            if (!s3d_gpu_register(framebuffer, t)) { s3d_gpu_lock(); s3d_gpu_detachLocked(f); s3d_gpu_unlock(); return 0; }
            return 1;
        }
    t = (S3dGpuTarget*)calloc(1, sizeof(S3dGpuTarget));
    if (t == NULL) { s3d_gpu_unlock(); return 0; }
    memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    ai.commandPool = g_s3dVk.cmdPool; ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; ai.commandBufferCount = 1;
    memset(&fi, 0, sizeof fi);
    fi.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
    memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
    if (!s3d_gpu_check(g_s3dVk.vkAllocateCommandBuffers(g_s3dVk.device, &ai, &t->cmd), "vkAllocateCommandBuffers")
        || !s3d_gpu_check(g_s3dVk.vkCreateFence(g_s3dVk.device, &fi, NULL, &t->fence), "vkCreateFence")
        || !s3d_gpu_check(g_s3dVk.vkCreateSemaphore(g_s3dVk.device, &si, NULL, &t->acquired), "vkCreateSemaphore")
        || !(t->set = s3d_gpu_textureSet(g_s3dVk.dummy.color.view, g_s3dVk.dummy.color.view, &t->pool))) {
        s3d_gpu_unlock();
        return 0;   /* (the little that was made stays with the device) */
    }
    t->attached = 1;
    t->framebuffer = framebuffer;
    t->frame = f;
    t->next = g_s3dVk.targets;
    g_s3dVk.targets = t;
    f->direct = t;
    s3d_gpu_unlock();
    /* (without the lock: a host may present right away) */
    if (!s3d_gpu_register(framebuffer, t)) {
        s3d_gpu_lock();
        s3d_gpu_detachLocked(f);
        s3d_gpu_unlock();
        return 0;
    }
    return 1;
}

/* The program ends: the windows are let go now (the driver cleans up after this, and the windows close after that - their presenters must not touch Vulkan any more then). */
static void s3d_gpu_atExit(void) {
    S3dGpuTarget* t;
    s3d_gpu_lock();
    if (g_s3dVk.state == 1) {
        s3d_gpu_idle();
        for (t = g_s3dVk.targets; t != NULL; t = t->next) { s3d_gpu_freeSurface(t); t->attached = 0; }
        g_s3dVk.state = -1;
        s3d_gpu_fail("The program has ended.");
    }
    g_s3dVk.ended = 1;
    s3d_gpu_unlock();
}

/* ---- letting go ---- */
static void s3d_gpu_releaseMesh(S3dMesh* mesh) {
    S3dGpuMesh* g = (S3dGpuMesh*)mesh->gpu;
    if (g == NULL) return;
    s3d_gpu_lock();
    s3d_gpu_idle();
    s3d_gpu_freeBuffer(&g->vb);
    s3d_gpu_freeBuffer(&g->ib);
    s3d_gpu_unlock();
    free(g);
    mesh->gpu = NULL;
}
static void s3d_gpu_releaseTexture(S3dTexture* tex) {
    S3dGpuTex* g = (S3dGpuTex*)tex->gpu;
    if (g == NULL) return;
    s3d_gpu_lock();
    s3d_gpu_idle();
    s3d_gpu_freeTex(g);
    s3d_gpu_unlock();
    free(g);
    tex->gpu = NULL;
}
static void s3d_gpu_releaseFrame(S3dFrame* f) {
    S3dGpuFrame* g;
    if (f->direct) s3d_gpu_detach(f);
    g = (S3dGpuFrame*)f->gpu;
    if (g == NULL) return;
    s3d_gpu_lock();
    s3d_gpu_idle();
    s3d_gpu_freeFrameTargets(g);
    s3d_gpu_unlock();
    free(g);
    f->gpu = NULL;
}
/* The end of a program (the VM keeps the library): the shadow maps go back to their smallest size; the device stays for the next program. */
static void s3d_gpu_shutdown(void) {
    s3d_gpu_lock();
    if (g_s3dVk.state == 1 && (g_s3dVk.shadowSize != 64 || g_s3dVk.shadowLayers != 1)) { s3d_gpu_idle(); s3d_gpu_freeShadows(); s3d_gpu_shadows(64, 1); }
    s3d_gpu_unlock();
}
static const char* s3d_gpu_deviceName(void) {
    int ready;
    s3d_gpu_lock();
    ready = s3d_gpu_ready();
    s3d_gpu_unlock();
    return ready ? g_s3dVk.deviceName : "";
}
static const char* s3d_gpu_error(void) { return g_s3dVk.state == -1 ? g_s3dVk.error : ""; }

#endif
#endif /* FIRE_S3D_GPU_VK_H */
