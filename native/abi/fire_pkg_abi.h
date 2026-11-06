/* fire package ABI (version 1): the C interface between the fire virtual machine (C#) and the natives of a package.
 *
 * A package that brings natives as C++ source (the way they are written for the native backend, see docs/packages/natives.md) is built by the compiler into a shared
 * library (.dll, .so, .dylib) with a generated wrapper around the functions. The wrapper exports the functions below; the virtual machine loads the library and calls
 * them. A package may also bring a prebuilt library (any language) for a platform: it only has to export these functions.
 *
 * Values cross the boundary as plain data. Numbers, text and arrays of these are COPIED: a native that changes an array it was given does not change the array of the caller
 * (return the changed array instead). Byte buffers are passed BY REFERENCE: the caller keeps them in place for the call (the VM pins them), the native reads and writes the bytes
 * directly - no copy, so a framebuffer can be handed over at any size; the length is fixed. Objects and pointers cannot cross. Units are not carried.
 * A LAMBDA crosses as a handle of the host (FIRE_LAMBDA): the library may keep it and call it back through fire_host.call_lambda - during a call on the VM's thread it
 * runs at once (nested, with the real globals), from another thread it runs at the VM's next safe point. A handle stays valid until the program ends.
 * Calls are serialized per library, but a native that calls a lambda back may be called again by it (on the same thread): the library is re-entrant there.
 */
#ifndef FIRE_PKG_ABI_H
#define FIRE_PKG_ABI_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum {
    FIRE_UNDEFINED = 0,
    FIRE_BOOL = 1,      /* v.i: 0 or 1 */
    FIRE_INT = 2,       /* v.i */
    FIRE_FLOAT = 3,     /* v.f */
    FIRE_CHAR = 4,      /* v.i: the code point */
    FIRE_STRING = 5,    /* v.p: UTF-16 (length UTF-16 code units, not zero terminated) */
    FIRE_ARRAY = 6,     /* v.p: `length` fire_val */
    FIRE_BUFFER = 7,    /* v.p: `length` bytes; an argument: the caller's memory (writable); a result: copied */
    FIRE_LAMBDA = 8,    /* v.i: a handle of the host; `length`: its number of parameters (an argument only) */
    FIRE_WORDS = 9,     /* v.p: `length` machine words of 64 bits (`word a[]` of the VM, SPEC 8.10); an argument: the caller's memory (writable); a result: copied */
    FIRE_WORD = 10      /* v.i: the 64 bits of a `word` */
};

typedef struct fire_val {
    int32_t kind;
    int32_t length;
    union { int64_t i; double f; const void* p; } v;
} fire_val;

#define FIRE_PKG_ABI_VERSION 1

/* A window that a package draws into itself (the scene3d package: a camera's pictures straight from the GPU, docs/graphics/3d.md "DirectDrawing"). The host (or, in a native build, the
 * windows bridge) gives the package the native handles of the window: what Vulkan (or another API) needs to make a surface for it. */
#ifndef FIRE_NATIVE_WINDOW_DEFINED
#define FIRE_NATIVE_WINDOW_DEFINED 1
enum { FIRE_NATIVE_NONE = 0, FIRE_NATIVE_WIN32 = 1, FIRE_NATIVE_X11 = 2, FIRE_NATIVE_WAYLAND = 3, FIRE_NATIVE_METAL = 4, FIRE_NATIVE_DCOMP = 5 };
typedef struct fire_native_window {
    int32_t kind;            /* FIRE_NATIVE_*: WIN32 a = HINSTANCE, b = HWND; X11 a = Display*, id = Window; WAYLAND a = wl_display*, b = wl_surface*; METAL b = CAMetalLayer*; DCOMP a = fire_dcomp_bridge*, b = HWND */
    int32_t width, height;   /* the size of the window in pixels */
    int32_t vsync;           /* the window's VSync */
    void* a;
    void* b;
    uint64_t id;
} fire_native_window;
/* The presenter of a package. The host calls it on the thread of the window, instead of showing the framebuffer: op 1 shows a picture - the package's, with the framebuffer's
 * pixels (width x height, R,G,B,A) laid over it where their alpha is not 0; op 0: let go of the window (it closes, or shows the framebuffer again). 1: shown; 0: it cannot -
 * the host shows the framebuffer itself, and does not ask again until the presenter is attached anew. */
typedef int32_t (*fire_present_fn)(void* ctx, int32_t op, const fire_native_window* window, const uint32_t* pixels, int32_t width, int32_t height);
/* FIRE_NATIVE_DCOMP (Windows, DirectComposition: docs/spec/UI_PLATFORM.md, part 4.4): the window shows a swap chain of DirectComposition - a package does not draw into the
 * HWND (b) but into the window's picture, a texture of D3D11 shared with it (a: a fire_dcomp_bridge): `texture` gives the handle of that picture at width x height
 * (R8G8B8A8; a shared handle of the kind D3D11_TEXTURE_KMT - made anew for another size, NULL: it cannot), the package draws into it and waits until it is done,
 * then `present` shows it (1: shown). `luid` is the adapter of D3D11 (the package's GPU must be the same); `premultiplied`: the window shows what lies behind it -
 * the picture is premultiplied by its alpha. */
typedef struct fire_dcomp_bridge {
    int32_t size;
    uint32_t luidLow; int32_t luidHigh;
    int32_t premultiplied;
    void* (*texture)(struct fire_dcomp_bridge* bridge, int32_t width, int32_t height);
    int32_t (*present)(struct fire_dcomp_bridge* bridge);
} fire_dcomp_bridge;
#endif

/* What the host decides, for natives that need it (the io package): which paths a script may touch and where the console goes. The host hands this structure to the library with
 * fire_pkg_set_host; every function may be null (then: everything is allowed, the console does nothing). They are called on the thread of the VM that called the native. */
typedef struct fire_host {
    int32_t size;                                                                                       /* sizeof(fire_host) */
    int32_t (*io_allow)(const char* path_utf8, int32_t access, char* reason, int32_t reason_size);    /* access bits: 1 read, 2 write, 4 delete, 8 list; 1 = allowed, 0 = refused (+ reason) */
    int32_t (*std_read)(int32_t stream, uint8_t* buffer, int32_t count);                              /* stream 0; bytes read, 0 at the end, < 0 error */
    int32_t (*std_write)(int32_t stream, const uint8_t* buffer, int32_t count);                       /* stream 1 output, 2 error; < 0 error */
    int32_t (*std_flush)(int32_t stream);
    /* The devices of the host (the device manager of the editor, its drivers, the packet trace): the natives of the devices package have no drivers of their own in the VM, they use these.
     * A device is a handle of the host; `size` tells which of these the host has (all of them from here on, or none). Received data is collected by the host (its drivers deliver on their own
     * threads): dev_poll takes the next packet. */
    int32_t (*dev_refresh)(int32_t fast_scan);
    int32_t (*dev_count)(void);
    int32_t (*dev_handle_at)(int32_t index);                                      /* -1: none */
    int32_t (*dev_identifier)(int32_t handle, char* buffer, int32_t size);       /* the length of the identifier (UTF-8), -1: no such device */
    int32_t (*dev_default)(void);                                                 /* the handle of the default device, -1: none */
    int32_t (*dev_manager_shared)(void);                                          /* 1: the manager belongs to the host and stays after the program */
    int32_t (*dev_shared)(int32_t handle);
    int32_t (*dev_availability)(int32_t handle);                                  /* 0 unavailable, 1 unchecked, 2 available */
    int32_t (*dev_test_availability)(int32_t handle);
    int32_t (*dev_connected)(int32_t handle);
    int32_t (*dev_port_name)(int32_t handle, char* buffer, int32_t size);
    int32_t (*dev_connect)(int32_t handle);                                       /* 1: connected */
    void (*dev_disconnect)(int32_t handle);
    int32_t (*dev_write)(int32_t handle, const uint8_t* data, int32_t count);     /* 1: sent */
    int32_t (*dev_send_command)(int32_t handle, const char* text_utf8);           /* a line: the text and a line ending in the encoding of the device; 1: sent */
    int32_t (*dev_poll)(int32_t handle, uint8_t* buffer, int32_t size);           /* the next received packet: its length (taken if it fits, else kept), -1: none */
    /* The network (the net package): may the script talk to this host and port? access bits: 1 connect (also: send a datagram), 2 listen (also: bind a UDP socket), 4 resolve a name
     * (port 0); 1 = allowed, 0 = refused (+ reason). A host that is older than this field has a smaller `size`: then everything is allowed. */
    int32_t (*net_allow)(const char* host_utf8, int32_t port, int32_t access, char* reason, int32_t reason_size);
    /* The windows of the host (the scene3d package, DirectDrawing): the windows that show the framebuffer `framebuffer` (an id of the graphics bridge) call `present` from now on
     * instead of showing it (1: the framebuffer exists); win_detach ends that (only for the same ctx). Called on the thread of the VM; `present` is called on the window's. */
    int32_t (*win_attach)(int32_t framebuffer, fire_present_fn present, void* ctx);
    void (*win_detach)(int32_t framebuffer, void* ctx);
    /* Lambdas (FIRE_LAMBDA): calls the lambda `handle` with `argc` arguments - on the VM's thread at once (0: it ran; 1: it threw, the VM reported it), from another thread at
     * the VM's next safe point (0: queued). The arguments are copied before it returns. */
    int32_t (*call_lambda)(int64_t handle, const fire_val* args, int32_t argc);
    /* From any thread: the VM calls fire_pkg_service of the library at its next safe point, on its thread (a package with a thread of its own: the windows' managers). */
    void (*request_service)(void);
    /* From any thread: adds (negative: removes) holds on the end of the program - it waits at its end, serving what is posted to it, until they are gone. */
    void (*hold)(int32_t delta);
} fire_host;

/* The entry points of a package library. */
int fire_pkg_abi_version(void);                    /* FIRE_PKG_ABI_VERSION */
int fire_pkg_function_count(void);
const char* fire_pkg_function_name(int index);     /* the name that fire code calls, e.g. "__sk_read" */
int fire_pkg_function_arity(int index);
/* Optional: the host of the VM tells the library what it decides (see fire_host); the structure stays valid as long as the library is loaded. */
void fire_pkg_set_host(const fire_host* host);
/* Optional: a program has ended, the library stays loaded for the next one: forget what the program left behind (open streams, ...). */
void fire_pkg_reset(void);
/* Optional: what the library asked for with fire_host.request_service (its host services), on the VM's thread at a safe point. */
void fire_pkg_service(void);
/* Calls function `index`. Returns 0, then `*result` is the result; 1, then `error` holds a message (zero terminated, at most errorSize bytes) and the program stops with it;
 * or 2, then the native throws an exception of the program: `error` holds the name of the exception class, a line feed and the message (the VM constructs the class with the message).
 * Memory behind `*result` (text, array, buffer) belongs to the library and stays valid until the next call from the same thread: the caller copies it. */
int fire_pkg_call(int index, const fire_val* args, int argc, fire_val* result, char* error, int errorSize);

#ifdef __cplusplus
}
#endif

#endif
