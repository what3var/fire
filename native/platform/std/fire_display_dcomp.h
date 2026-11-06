/* fire native platform package "std": the frames of a Windows window through DirectComposition (Windows 8 and later) - a bridge to the compositor, not a scene: one
 * visual per window, its content a swap chain of DXGI "for composition" (R8G8B8A8; alpha ignored for an opaque window, premultiplied for one that shows what lies
 * behind it: DWM mixes it - also with Mica and acrylic). What the window shows is drawn already (by the CPU: the framebuffer's pixels; by the GPU: Vulkan, see below);
 * this only hands it to DWM. docs/spec/UI_PLATFORM.md, part 4.4.
 *
 * When it is there (fire_dcomp_available: d3d11.dll, dxgi.dll and dcomp.dll load, a D3D11 device and a DirectComposition device can be made; FIRE_DCOMP=0 switches
 * it off), every window of the display gets WS_EX_NOREDIRECTIONBITMAP - it has no bitmap of GDI - and shows all its frames this way: the pixels go into the window's
 * picture (a D3D11 texture: only the area that changed, UpdateSubresource), which is copied into the back buffer of the swap chain and presented (Present waits
 * for the screen with VSync). A framebuffer of another size than the window is stretched by the visual (nearest).
 * A package that draws with the GPU (graphics' presenter, part 4.4) gets the window as FIRE_NATIVE_DCOMP: the picture is a shared texture (a handle of the kind
 * D3D11_TEXTURE_KMT, imported by Vulkan), the package copies its frame into it and lets the bridge present - nothing comes back to the CPU.
 * If the device is lost (DXGI_ERROR_DEVICE_REMOVED), DirectComposition is off for the process: the display makes its windows anew without it (GDI).
 *
 * COM in C: the methods are called through their place in the vtable (FIRE_DC_METHOD); the places and the structures are checked against the headers of the
 * Windows SDK by tools/check_dcomp.sh (MinGW, run under Wine). Nothing is linked: a toolchain without the headers (TinyCC) builds it all the same. */
#ifndef FIRE_DISPLAY_DCOMP_H
#define FIRE_DISPLAY_DCOMP_H

#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/* ---- the structures and numbers (DXGI, D3D11, DirectComposition) ---- */
typedef struct FireDcGuid { unsigned long d1; unsigned short d2, d3; unsigned char d4[8]; } FireDcGuid;
typedef struct FireDcSample { UINT count, quality; } FireDcSample;
typedef struct FireDcTexDesc { UINT width, height, mipLevels, arraySize; int format; FireDcSample sample; int usage; UINT bindFlags, cpuAccess, miscFlags; } FireDcTexDesc;   /* D3D11_TEXTURE2D_DESC */
typedef struct FireDcSwapDesc { UINT width, height; int format; BOOL stereo; FireDcSample sample; UINT bufferUsage, bufferCount; int scaling, swapEffect, alphaMode; UINT flags; } FireDcSwapDesc;   /* DXGI_SWAP_CHAIN_DESC1 */
typedef struct FireDcBox { UINT left, top, front, right, bottom, back; } FireDcBox;   /* D3D11_BOX */
typedef struct FireDcQueryDesc { int query; UINT miscFlags; } FireDcQueryDesc;   /* D3D11_QUERY_DESC */
typedef struct FireDcMatrix { float m11, m12, m21, m22, dx, dy; } FireDcMatrix;   /* D2D_MATRIX_3X2_F */
enum { FIRE_DC_RGBA = 28, FIRE_DC_ALPHA_PREMULTIPLIED = 1, FIRE_DC_ALPHA_IGNORE = 3, FIRE_DC_FLIP_SEQUENTIAL = 3, FIRE_DC_RENDER_TARGET_OUTPUT = 0x20 };
#define FIRE_DC_DEVICE_REMOVED ((HRESULT)0x887A0005L)
#define FIRE_DC_DEVICE_RESET ((HRESULT)0x887A0007L)
#define FIRE_DC_DESC_LUID 296   /* (where DXGI_ADAPTER_DESC keeps AdapterLuid; the structure is 304 bytes) */

static const FireDcGuid FIRE_IID_IDXGIDevice = { 0x54ec77fa, 0x1377, 0x44e6, { 0x8c, 0x32, 0x88, 0xfd, 0x5f, 0x44, 0xc8, 0x4c } };
static const FireDcGuid FIRE_IID_IDXGIFactory2 = { 0x50c83a1c, 0xe072, 0x4c48, { 0x87, 0xb0, 0x36, 0x30, 0xfa, 0x36, 0xa6, 0xd0 } };
static const FireDcGuid FIRE_IID_ID3D11Texture2D = { 0x6f15aaf2, 0xd208, 0x4e89, { 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c } };
static const FireDcGuid FIRE_IID_IDXGIResource = { 0x035f3ab4, 0x482e, 0x4e50, { 0xb4, 0x1f, 0x8a, 0x7f, 0x8b, 0xd8, 0x96, 0x0b } };
static const FireDcGuid FIRE_IID_IDCompositionDevice = { 0xc37ea93a, 0xe7aa, 0x450d, { 0xb1, 0x6f, 0x97, 0x46, 0xcb, 0x04, 0x07, 0xf3 } };

/* A method of a COM object by its place in the vtable (IUnknown: 0 QueryInterface, 1 AddRef, 2 Release). */
#define FIRE_DC_METHOD(obj, slot, type) ((type)((*(void***)(obj))[slot]))
#define FIRE_DC_RELEASE(obj) do { if ((obj) != NULL) { FIRE_DC_METHOD((obj), 2, ULONG (STDMETHODCALLTYPE*)(void*))(obj); (obj) = NULL; } } while (0)
/* (the places: tools/check_dcomp.sh compares them with the SDK) */
enum {
    FIRE_DC_QI = 0,
    FIRE_DC_DEV_CREATE_TEXTURE2D = 5, FIRE_DC_DEV_CREATE_QUERY = 24,
    FIRE_DC_CTX_END = 28, FIRE_DC_CTX_GET_DATA = 29, FIRE_DC_CTX_COPY_RESOURCE = 47, FIRE_DC_CTX_UPDATE_SUBRESOURCE = 48, FIRE_DC_CTX_FLUSH = 111,
    FIRE_DC_DXGIDEV_GET_ADAPTER = 7, FIRE_DC_OBJ_GET_PARENT = 6, FIRE_DC_ADAPTER_GET_DESC = 8, FIRE_DC_RES_GET_SHARED_HANDLE = 8,
    FIRE_DC_CHAIN_PRESENT = 8, FIRE_DC_CHAIN_GET_BUFFER = 9, FIRE_DC_CHAIN_RESIZE_BUFFERS = 13, FIRE_DC_FACTORY2_FOR_COMPOSITION = 24,
    FIRE_DC_COMP_COMMIT = 3, FIRE_DC_COMP_TARGET_FOR_HWND = 6, FIRE_DC_COMP_CREATE_VISUAL = 7, FIRE_DC_TARGET_SET_ROOT = 3,
    FIRE_DC_VISUAL_SET_TRANSFORM_MATRIX = 8, FIRE_DC_VISUAL_SET_INTERPOLATION = 11, FIRE_DC_VISUAL_SET_CONTENT = 15
};

/* ---- the process: one D3D11 device for all windows (its context is used under the lock) ---- */
typedef struct FireDcomp {
    int tried, ok, broken;
    HMODULE d3d11, dcompLib;
    HRESULT (WINAPI *D3D11CreateDevice)(void*, int, HMODULE, UINT, const int*, UINT, UINT, void**, int*, void**);
    HRESULT (WINAPI *DCompositionCreateDevice)(void*, const FireDcGuid*, void**);
    void* device;      /* ID3D11Device */
    void* context;     /* ID3D11DeviceContext */
    void* dxgiDevice;  /* IDXGIDevice */
    void* factory;     /* IDXGIFactory2 */
    void* query;       /* ID3D11Query (an event: the copies of a frame are done) */
    uint32_t luidLow; int32_t luidHigh;
    CRITICAL_SECTION lock;
} FireDcomp;

static FireDcomp g_fireDcomp;

static void fire_dcomp_proc(HMODULE m, const char* name, void* fn) { FARPROC p = m != NULL ? GetProcAddress(m, name) : NULL; memcpy(fn, &p, sizeof p); }

/* Whether the windows show their frames through DirectComposition (decided once, before the first window: its style depends on it). */
static int fire_dcomp_available(void) {
    FireDcomp* c = &g_fireDcomp;
    const char* env;
    if (c->tried) return c->ok && !c->broken;
    c->tried = 1;
    env = getenv("FIRE_DCOMP");
    if (env != NULL && strcmp(env, "0") == 0) return 0;
    c->d3d11 = LoadLibraryW(L"d3d11.dll");
    c->dcompLib = LoadLibraryW(L"dcomp.dll");
    fire_dcomp_proc(c->d3d11, "D3D11CreateDevice", &c->D3D11CreateDevice);
    fire_dcomp_proc(c->dcompLib, "DCompositionCreateDevice", &c->DCompositionCreateDevice);
    if (c->D3D11CreateDevice == NULL || c->DCompositionCreateDevice == NULL) return 0;
    {
        int level = 0;
        void* adapter = NULL;
        void* probe = NULL;
        unsigned char desc[304];
        FireDcQueryDesc qd;
        /* the GPU (BGRA support: what DirectComposition asks for), else WARP (the CPU: the pixels still go to DWM) */
        if (c->D3D11CreateDevice(NULL, 1 /* HARDWARE */, NULL, 0x20, NULL, 0, 7, &c->device, &level, &c->context) != S_OK
            && c->D3D11CreateDevice(NULL, 5 /* WARP */, NULL, 0x20, NULL, 0, 7, &c->device, &level, &c->context) != S_OK) { c->device = c->context = NULL; return 0; }
        if (FIRE_DC_METHOD(c->device, FIRE_DC_QI, HRESULT (STDMETHODCALLTYPE*)(void*, const FireDcGuid*, void**))(c->device, &FIRE_IID_IDXGIDevice, &c->dxgiDevice) != S_OK) c->dxgiDevice = NULL;
        if (c->dxgiDevice != NULL && FIRE_DC_METHOD(c->dxgiDevice, FIRE_DC_DXGIDEV_GET_ADAPTER, HRESULT (STDMETHODCALLTYPE*)(void*, void**))(c->dxgiDevice, &adapter) == S_OK && adapter != NULL) {
            memset(desc, 0, sizeof desc);
            if (FIRE_DC_METHOD(adapter, FIRE_DC_ADAPTER_GET_DESC, HRESULT (STDMETHODCALLTYPE*)(void*, void*))(adapter, desc) == S_OK) {
                memcpy(&c->luidLow, desc + FIRE_DC_DESC_LUID, 4);
                memcpy(&c->luidHigh, desc + FIRE_DC_DESC_LUID + 4, 4);
            }
            if (FIRE_DC_METHOD(adapter, FIRE_DC_OBJ_GET_PARENT, HRESULT (STDMETHODCALLTYPE*)(void*, const FireDcGuid*, void**))(adapter, &FIRE_IID_IDXGIFactory2, &c->factory) != S_OK) c->factory = NULL;
            FIRE_DC_RELEASE(adapter);
        }
        /* (DirectComposition must really be there: Wine's, for one, is not) */
        if (c->dxgiDevice != NULL && c->factory != NULL && c->DCompositionCreateDevice(c->dxgiDevice, &FIRE_IID_IDCompositionDevice, &probe) == S_OK && probe != NULL) {
            FIRE_DC_RELEASE(probe);
            qd.query = 0; qd.miscFlags = 0;   /* D3D11_QUERY_EVENT */
            if (FIRE_DC_METHOD(c->device, FIRE_DC_DEV_CREATE_QUERY, HRESULT (STDMETHODCALLTYPE*)(void*, const FireDcQueryDesc*, void**))(c->device, &qd, &c->query) != S_OK) c->query = NULL;
            InitializeCriticalSection(&c->lock);
            c->ok = 1;
        }
        if (!c->ok) { FIRE_DC_RELEASE(c->factory); FIRE_DC_RELEASE(c->dxgiDevice); FIRE_DC_RELEASE(c->context); FIRE_DC_RELEASE(c->device); }
    }
    return c->ok;
}

/* ---- a window ---- */
typedef struct FireDcompWindow {
    fire_dcomp_bridge bridge;   /* (first: a presenter gets its address) */
    int on;                     /* the window shows its frames this way */
    int lost;                   /* the device is gone: the display makes the window anew without it */
    HWND hwnd;
    void* dev;                  /* IDCompositionDevice */
    void* target;               /* IDCompositionTarget */
    void* visual;               /* IDCompositionVisual */
    void* chain;                /* IDXGISwapChain1 */
    void* content;              /* ID3D11Texture2D: the window's picture (shared) */
    HANDLE shared;
    int chainW, chainH, alpha;
    int contentW, contentH, full;   /* `full`: the picture is not the last frame (made anew, or a presenter drew into it): the next pixels go in whole */
    int viewW, viewH, viewMadeW, viewMadeH;   /* the size of the window, and the one the visual's transform was made for */
    int premultiplied, vsync;
    uint32_t* row; int rowCap;  /* a row premultiplied */
} FireDcompWindow;

static void* fire_dcomp_bridgeTexture(fire_dcomp_bridge* b, int32_t width, int32_t height);
static int32_t fire_dcomp_bridgePresent(fire_dcomp_bridge* b);

/* DirectComposition for the window (made with WS_EX_NOREDIRECTIONBITMAP): its device, the target and the visual. 0: it cannot (the window shows nothing then - the
 * display makes it anew without). */
static int fire_dcomp_open(FireDcompWindow* d, HWND hwnd) {
    FireDcomp* c = &g_fireDcomp;
    memset(d, 0, sizeof *d);
    if (!fire_dcomp_available()) return 0;
    d->hwnd = hwnd;
    EnterCriticalSection(&c->lock);
    if (c->DCompositionCreateDevice(c->dxgiDevice, &FIRE_IID_IDCompositionDevice, &d->dev) != S_OK) d->dev = NULL;
    if (d->dev != NULL && FIRE_DC_METHOD(d->dev, FIRE_DC_COMP_TARGET_FOR_HWND, HRESULT (STDMETHODCALLTYPE*)(void*, HWND, BOOL, void**))(d->dev, hwnd, TRUE, &d->target) != S_OK) d->target = NULL;
    if (d->target != NULL && FIRE_DC_METHOD(d->dev, FIRE_DC_COMP_CREATE_VISUAL, HRESULT (STDMETHODCALLTYPE*)(void*, void**))(d->dev, &d->visual) != S_OK) d->visual = NULL;
    if (d->visual != NULL) {
        FIRE_DC_METHOD(d->visual, FIRE_DC_VISUAL_SET_INTERPOLATION, HRESULT (STDMETHODCALLTYPE*)(void*, int))(d->visual, 0);   /* (nearest, when it is stretched) */
        if (FIRE_DC_METHOD(d->target, FIRE_DC_TARGET_SET_ROOT, HRESULT (STDMETHODCALLTYPE*)(void*, void*))(d->target, d->visual) == S_OK
            && FIRE_DC_METHOD(d->dev, FIRE_DC_COMP_COMMIT, HRESULT (STDMETHODCALLTYPE*)(void*))(d->dev) == S_OK) d->on = 1;
    }
    LeaveCriticalSection(&c->lock);
    if (!d->on) { FIRE_DC_RELEASE(d->visual); FIRE_DC_RELEASE(d->target); FIRE_DC_RELEASE(d->dev); return 0; }
    d->bridge.size = (int32_t)sizeof d->bridge;
    d->bridge.luidLow = c->luidLow; d->bridge.luidHigh = c->luidHigh;
    d->bridge.texture = fire_dcomp_bridgeTexture;
    d->bridge.present = fire_dcomp_bridgePresent;
    d->full = 1;
    return 1;
}

static void fire_dcomp_close(FireDcompWindow* d) {
    FireDcomp* c = &g_fireDcomp;
    if (d->dev == NULL && d->chain == NULL && d->content == NULL) { free(d->row); memset(d, 0, sizeof *d); return; }
    EnterCriticalSection(&c->lock);
    FIRE_DC_RELEASE(d->content);
    FIRE_DC_RELEASE(d->chain);
    FIRE_DC_RELEASE(d->visual);
    FIRE_DC_RELEASE(d->target);
    FIRE_DC_RELEASE(d->dev);
    LeaveCriticalSection(&c->lock);
    free(d->row);
    memset(d, 0, sizeof *d);
}

/* The device went away: DirectComposition is off for the process, the windows go back to GDI. */
static int fire_dcomp_failed(FireDcompWindow* d, HRESULT hr) {
    if (hr == FIRE_DC_DEVICE_REMOVED || hr == FIRE_DC_DEVICE_RESET) { g_fireDcomp.broken = 1; d->lost = 1; }
    return 0;
}

/* The swap chain and the picture for frames of width x height (made anew for another size or alpha), and the visual's transform for the size of the window. The lock
 * is held. 0: it failed. */
static int fire_dcomp_ensure(FireDcompWindow* d, int width, int height) {
    FireDcomp* c = &g_fireDcomp;
    int alpha = d->premultiplied ? FIRE_DC_ALPHA_PREMULTIPLIED : FIRE_DC_ALPHA_IGNORE;
    HRESULT hr;
    if (width <= 0 || height <= 0 || d->lost || c->broken) return 0;
    if (d->chain != NULL && d->alpha != alpha) FIRE_DC_RELEASE(d->chain);   /* (the alpha of a swap chain cannot change) */
    if (d->chain == NULL) {
        FireDcSwapDesc sd;
        memset(&sd, 0, sizeof sd);
        sd.width = (UINT)width; sd.height = (UINT)height;
        sd.format = FIRE_DC_RGBA;
        sd.sample.count = 1;
        sd.bufferUsage = FIRE_DC_RENDER_TARGET_OUTPUT;
        sd.bufferCount = 2;
        sd.scaling = 0;   /* DXGI_SCALING_STRETCH (the only one for composition) */
        sd.swapEffect = FIRE_DC_FLIP_SEQUENTIAL;
        sd.alphaMode = alpha;
        hr = FIRE_DC_METHOD(c->factory, FIRE_DC_FACTORY2_FOR_COMPOSITION, HRESULT (STDMETHODCALLTYPE*)(void*, void*, const FireDcSwapDesc*, void*, void**))(c->factory, c->device, &sd, NULL, &d->chain);
        if (hr != S_OK) { d->chain = NULL; return fire_dcomp_failed(d, hr); }
        if (FIRE_DC_METHOD(d->visual, FIRE_DC_VISUAL_SET_CONTENT, HRESULT (STDMETHODCALLTYPE*)(void*, void*))(d->visual, d->chain) != S_OK) { FIRE_DC_RELEASE(d->chain); return 0; }
        d->chainW = width; d->chainH = height; d->alpha = alpha;
        d->viewMadeW = d->viewMadeH = -1;   /* (committed below) */
    } else if (d->chainW != width || d->chainH != height) {
        hr = FIRE_DC_METHOD(d->chain, FIRE_DC_CHAIN_RESIZE_BUFFERS, HRESULT (STDMETHODCALLTYPE*)(void*, UINT, UINT, UINT, int, UINT))(d->chain, 0, (UINT)width, (UINT)height, 0, 0);
        if (hr != S_OK) return fire_dcomp_failed(d, hr);
        d->chainW = width; d->chainH = height;
        d->viewMadeW = d->viewMadeH = -1;
    }
    if (d->content == NULL || d->contentW != width || d->contentH != height) {
        FireDcTexDesc td;
        void* res = NULL;
        FIRE_DC_RELEASE(d->content);
        d->shared = NULL;
        memset(&td, 0, sizeof td);
        td.width = (UINT)width; td.height = (UINT)height; td.mipLevels = 1; td.arraySize = 1;
        td.format = FIRE_DC_RGBA;
        td.sample.count = 1;
        td.usage = 0;   /* D3D11_USAGE_DEFAULT */
        td.bindFlags = 0x8 | 0x20;   /* SHADER_RESOURCE | RENDER_TARGET (what a shared texture needs) */
        td.miscFlags = 0x2;          /* D3D11_RESOURCE_MISC_SHARED */
        hr = FIRE_DC_METHOD(c->device, FIRE_DC_DEV_CREATE_TEXTURE2D, HRESULT (STDMETHODCALLTYPE*)(void*, const FireDcTexDesc*, const void*, void**))(c->device, &td, NULL, &d->content);
        if (hr != S_OK) { d->content = NULL; return fire_dcomp_failed(d, hr); }
        if (FIRE_DC_METHOD(d->content, FIRE_DC_QI, HRESULT (STDMETHODCALLTYPE*)(void*, const FireDcGuid*, void**))(d->content, &FIRE_IID_IDXGIResource, &res) == S_OK && res != NULL) {
            if (FIRE_DC_METHOD(res, FIRE_DC_RES_GET_SHARED_HANDLE, HRESULT (STDMETHODCALLTYPE*)(void*, HANDLE*))(res, &d->shared) != S_OK) d->shared = NULL;
            FIRE_DC_RELEASE(res);
        }
        d->contentW = width; d->contentH = height;
        d->full = 1;
    }
    if (d->viewMadeW != d->viewW || d->viewMadeH != d->viewH) {
        /* the frame stretched to the window (when their sizes differ) */
        FireDcMatrix m;
        memset(&m, 0, sizeof m);
        m.m11 = d->viewW > 0 ? (float)d->viewW / (float)width : 1.0f;
        m.m22 = d->viewH > 0 ? (float)d->viewH / (float)height : 1.0f;
        FIRE_DC_METHOD(d->visual, FIRE_DC_VISUAL_SET_TRANSFORM_MATRIX, HRESULT (STDMETHODCALLTYPE*)(void*, const FireDcMatrix*))(d->visual, &m);
        FIRE_DC_METHOD(d->dev, FIRE_DC_COMP_COMMIT, HRESULT (STDMETHODCALLTYPE*)(void*))(d->dev);
        d->viewMadeW = d->viewW; d->viewMadeH = d->viewH;
    }
    return 1;
}

/* The picture into the back buffer, presented (with VSync: at the screen's beat); `wait`: until the GPU is done with the copy (a presenter draws into the picture
 * next). The lock is held. */
static int fire_dcomp_show(FireDcompWindow* d, int wait) {
    FireDcomp* c = &g_fireDcomp;
    void* back = NULL;
    HRESULT hr;
    hr = FIRE_DC_METHOD(d->chain, FIRE_DC_CHAIN_GET_BUFFER, HRESULT (STDMETHODCALLTYPE*)(void*, UINT, const FireDcGuid*, void**))(d->chain, 0, &FIRE_IID_ID3D11Texture2D, &back);
    if (hr != S_OK || back == NULL) return fire_dcomp_failed(d, hr);
    FIRE_DC_METHOD(c->context, FIRE_DC_CTX_COPY_RESOURCE, void (STDMETHODCALLTYPE*)(void*, void*, void*))(c->context, back, d->content);
    FIRE_DC_RELEASE(back);
    hr = FIRE_DC_METHOD(d->chain, FIRE_DC_CHAIN_PRESENT, HRESULT (STDMETHODCALLTYPE*)(void*, UINT, UINT))(d->chain, d->vsync ? 1u : 0u, 0u);
    if (hr != S_OK && hr != (HRESULT)0x087A0001L /* DXGI_STATUS_OCCLUDED */) return fire_dcomp_failed(d, hr);
    if (wait && c->query != NULL) {
        int spins = 0;
        FIRE_DC_METHOD(c->context, FIRE_DC_CTX_END, void (STDMETHODCALLTYPE*)(void*, void*))(c->context, c->query);
        FIRE_DC_METHOD(c->context, FIRE_DC_CTX_FLUSH, void (STDMETHODCALLTYPE*)(void*))(c->context);
        while (FIRE_DC_METHOD(c->context, FIRE_DC_CTX_GET_DATA, HRESULT (STDMETHODCALLTYPE*)(void*, void*, void*, UINT, UINT))(c->context, c->query, NULL, 0, 0) == S_FALSE && spins++ < 200000)
            Sleep(0);
    }
    return 1;
}

/* Shows pixels (R,G,B,A per word, width x height; only the area ax, ay, aw, ah changed since the last frame) - premultiplied when the window shows what lies behind
 * it. 0: it failed (lost: the display goes back to GDI). */
static int fire_dcomp_presentPixels(FireDcompWindow* d, const uint32_t* rgba, int width, int height, int ax, int ay, int aw, int ah) {
    FireDcomp* c = &g_fireDcomp;
    int ok, r0, r1, c0, c1, row, col;
    if (!d->on) return 0;
    EnterCriticalSection(&c->lock);
    ok = fire_dcomp_ensure(d, width, height);
    if (ok) {
        if (d->full) { c0 = 0; c1 = width; r0 = 0; r1 = height; d->full = 0; }
        else {
            c0 = ax < 0 ? 0 : ax > width ? width : ax; c1 = ax + aw < 0 ? 0 : ax + aw > width ? width : ax + aw;
            r0 = ay < 0 ? 0 : ay > height ? height : ay; r1 = ay + ah < 0 ? 0 : ay + ah > height ? height : ay + ah;
        }
        if (c1 > c0 && r1 > r0) {
            if (!d->premultiplied) {
                FireDcBox box = { (UINT)c0, (UINT)r0, 0, (UINT)c1, (UINT)r1, 1 };
                FIRE_DC_METHOD(c->context, FIRE_DC_CTX_UPDATE_SUBRESOURCE, void (STDMETHODCALLTYPE*)(void*, void*, UINT, const FireDcBox*, const void*, UINT, UINT))(
                    c->context, d->content, 0, &box, rgba + (size_t)r0 * width + c0, (UINT)width * 4u, 0);
            } else {
                /* (row by row: each one premultiplied first, as the GDI path does it) */
                if (d->rowCap < c1 - c0) {
                    uint32_t* grown = (uint32_t*)realloc(d->row, sizeof(uint32_t) * (size_t)(c1 - c0));
                    if (grown != NULL) { d->row = grown; d->rowCap = c1 - c0; }
                }
                if (d->rowCap >= c1 - c0)
                    for (row = r0; row < r1; row++) {
                        const uint32_t* src = rgba + (size_t)row * width;
                        FireDcBox box = { (UINT)c0, (UINT)row, 0, (UINT)c1, (UINT)row + 1u, 1 };
                        for (col = c0; col < c1; col++) {
                            uint32_t v = src[col], a = v >> 24;
                            d->row[col - c0] = (a << 24) | (((v >> 16) & 0xFFu) * a / 255u) << 16 | (((v >> 8) & 0xFFu) * a / 255u) << 8 | ((v & 0xFFu) * a / 255u);
                        }
                        FIRE_DC_METHOD(c->context, FIRE_DC_CTX_UPDATE_SUBRESOURCE, void (STDMETHODCALLTYPE*)(void*, void*, UINT, const FireDcBox*, const void*, UINT, UINT))(
                            c->context, d->content, 0, &box, d->row, (UINT)(c1 - c0) * 4u, 0);
                    }
            }
        }
        ok = fire_dcomp_show(d, 0);
    }
    LeaveCriticalSection(&c->lock);
    return ok;
}

/* ---- the bridge of a presenter (fire_dcomp_bridge, on the window's thread) ---- */
static void* fire_dcomp_bridgeTexture(fire_dcomp_bridge* b, int32_t width, int32_t height) {
    FireDcompWindow* d = (FireDcompWindow*)(void*)b;
    FireDcomp* c = &g_fireDcomp;
    void* handle = NULL;
    if (!d->on) return NULL;
    EnterCriticalSection(&c->lock);
    if (fire_dcomp_ensure(d, width, height)) handle = d->shared;
    d->full = 1;   /* (the presenter draws into it: pixels that come later go in whole) */
    LeaveCriticalSection(&c->lock);
    return handle;
}
static int32_t fire_dcomp_bridgePresent(fire_dcomp_bridge* b) {
    FireDcompWindow* d = (FireDcompWindow*)(void*)b;
    FireDcomp* c = &g_fireDcomp;
    int ok;
    if (!d->on || d->chain == NULL || d->content == NULL) return 0;
    EnterCriticalSection(&c->lock);
    ok = fire_dcomp_show(d, 1);
    LeaveCriticalSection(&c->lock);
    return ok;
}

#endif
