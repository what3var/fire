/* fire native platform package "std": a window on a Wayland desktop (fire_dwl_*, one of the displays of fire_display_desktop.h) - without the headers of
 * Wayland: libwayland-client and libxkbcommon are loaded when the first window opens (dlopen; libwayland-cursor is optional), the protocols beyond the core
 * (xdg-shell, xdg-decoration, viewporter, fractional-scale, text-input-v3, cursor-shape, ext-background-effect, KDE's blur) are described by tables of our own.
 *
 * Every window has its own connection to the compositor (it belongs to the thread that opened it; the clipboard borrows the connection of the window that has the
 * keyboard, under the window's lock). What the window shows is the framebuffer, stretched into a buffer of shared memory (wl_shm, two of them) of the window's size
 * in pixels of the screen (the scale of the output, also a fractional one with wp_fractional_scale + wp_viewporter); positions are reported in logical pixels of
 * the window, as the other displays report pixels of their window. VSync: a frame waits for the compositor's frame callback of the one before.
 * Decorations: the compositor's (xdg-decoration, server side) - where it draws none (GNOME), the window draws a plain title bar of its own above the framebuffer
 * (FIRE_WAYLAND_CSD=0 leaves it out: tests). A window with that title bar is not given to a presenter (fire_display_native: a GPU drawing into the surface would
 * leave it out) - it shows the pixels
 * (move, resize at the edges, minimize, maximize, close; the title with the built-in font of the graphics bridge when the program has it).
 * Input: keys (the scan code from the evdev code, the key code and the text from the keymap of the compositor through libxkbcommon, dead keys with its compose
 * tables, repeat at the rate the compositor names), text through the input method of the compositor (text-input-v3: compositions as FIRE_EV_TEXT_EDITING), the
 * mouse and the wheel, fingers, what other programs drop (wl_data_device: files as text/uri-list, text as UTF-8), the cursors of the compositor (cursor-shape)
 * or of the theme (libwayland-cursor).
 * Behind the window: the buffer has alpha, so the window can be transparent; the compositor blurs behind it where it can (ext-background-effect, KDE's blur). */
#ifndef FIRE_DISPLAY_WAYLAND_H
#define FIRE_DISPLAY_WAYLAND_H

#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#ifdef __linux__
#  include <sys/syscall.h>
#endif
#include "fire_display_common.h"
#include "fire_systheme.h"
#ifndef FIRE_DRAGOUT_WANT_WAYLAND
#  define FIRE_DRAGOUT_WANT_WAYLAND 1
#endif
#include "fire_dragout.h"

/* ---- the descriptions of interfaces, laid out as struct wl_interface and struct wl_message of libwayland ---- */
typedef struct FireWlMessage { const char* name; const char* signature; const void* const* types; } FireWlMessage;
typedef struct FireWlIface { const char* name; int version; int method_count; const FireWlMessage* methods; int event_count; const FireWlMessage* events; } FireWlIface;
typedef struct FireWlArray { size_t size, alloc; void* data; } FireWlArray;
typedef void (*FireWlVoid)(void);

/* (what libwayland reads of `types`: nothing for our messages but a slot per argument - all NULL, the new objects get their interface from the call) */
static const void* const fire_wlNoTypes[8] = { NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL };
#define FIRE_WLM(name, sig) { name, sig, fire_wlNoTypes }

static const FireWlMessage fire_wlXdgWmBaseReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("create_positioner", "n"), FIRE_WLM("get_xdg_surface", "no"), FIRE_WLM("pong", "u") };
static const FireWlMessage fire_wlXdgWmBaseEv[] = { FIRE_WLM("ping", "u") };
static const FireWlIface fire_wlXdgWmBase = { "xdg_wm_base", 3, 4, fire_wlXdgWmBaseReq, 1, fire_wlXdgWmBaseEv };
static const FireWlMessage fire_wlXdgSurfaceReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("get_toplevel", "n"), FIRE_WLM("get_popup", "n?oo"), FIRE_WLM("set_window_geometry", "iiii"), FIRE_WLM("ack_configure", "u") };
static const FireWlMessage fire_wlXdgSurfaceEv[] = { FIRE_WLM("configure", "u") };
static const FireWlIface fire_wlXdgSurface = { "xdg_surface", 3, 5, fire_wlXdgSurfaceReq, 1, fire_wlXdgSurfaceEv };
static const FireWlMessage fire_wlXdgToplevelReq[] = {
    FIRE_WLM("destroy", ""), FIRE_WLM("set_parent", "?o"), FIRE_WLM("set_title", "s"), FIRE_WLM("set_app_id", "s"), FIRE_WLM("show_window_menu", "ouii"), FIRE_WLM("move", "ou"),
    FIRE_WLM("resize", "ouu"), FIRE_WLM("set_max_size", "ii"), FIRE_WLM("set_min_size", "ii"), FIRE_WLM("set_maximized", ""), FIRE_WLM("unset_maximized", ""),
    FIRE_WLM("set_fullscreen", "?o"), FIRE_WLM("unset_fullscreen", ""), FIRE_WLM("set_minimized", "")
};
static const FireWlMessage fire_wlXdgToplevelEv[] = { FIRE_WLM("configure", "iia"), FIRE_WLM("close", "") };
static const FireWlIface fire_wlXdgToplevel = { "xdg_toplevel", 3, 14, fire_wlXdgToplevelReq, 2, fire_wlXdgToplevelEv };
static const FireWlMessage fire_wlDecoMgrReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("get_toplevel_decoration", "no") };
static const FireWlIface fire_wlDecoMgr = { "zxdg_decoration_manager_v1", 1, 2, fire_wlDecoMgrReq, 0, NULL };
static const FireWlMessage fire_wlDecoReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("set_mode", "u"), FIRE_WLM("unset_mode", "") };
static const FireWlMessage fire_wlDecoEv[] = { FIRE_WLM("configure", "u") };
static const FireWlIface fire_wlDeco = { "zxdg_toplevel_decoration_v1", 1, 3, fire_wlDecoReq, 1, fire_wlDecoEv };
static const FireWlMessage fire_wlViewporterReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("get_viewport", "no") };
static const FireWlIface fire_wlViewporter = { "wp_viewporter", 1, 2, fire_wlViewporterReq, 0, NULL };
static const FireWlMessage fire_wlViewportReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("set_source", "ffff"), FIRE_WLM("set_destination", "ii") };
static const FireWlIface fire_wlViewport = { "wp_viewport", 1, 3, fire_wlViewportReq, 0, NULL };
static const FireWlMessage fire_wlFracMgrReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("get_fractional_scale", "no") };
static const FireWlIface fire_wlFracMgr = { "wp_fractional_scale_manager_v1", 1, 2, fire_wlFracMgrReq, 0, NULL };
static const FireWlMessage fire_wlFracReq[] = { FIRE_WLM("destroy", "") };
static const FireWlMessage fire_wlFracEv[] = { FIRE_WLM("preferred_scale", "u") };
static const FireWlIface fire_wlFrac = { "wp_fractional_scale_v1", 1, 1, fire_wlFracReq, 1, fire_wlFracEv };
static const FireWlMessage fire_wlTextMgrReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("get_text_input", "no") };
static const FireWlIface fire_wlTextMgr = { "zwp_text_input_manager_v3", 1, 2, fire_wlTextMgrReq, 0, NULL };
static const FireWlMessage fire_wlTextReq[] = {
    FIRE_WLM("destroy", ""), FIRE_WLM("enable", ""), FIRE_WLM("disable", ""), FIRE_WLM("set_surrounding_text", "sii"), FIRE_WLM("set_text_change_cause", "u"),
    FIRE_WLM("set_content_type", "uu"), FIRE_WLM("set_cursor_rectangle", "iiii"), FIRE_WLM("commit", "")
};
static const FireWlMessage fire_wlTextEv[] = {
    FIRE_WLM("enter", "o"), FIRE_WLM("leave", "o"), FIRE_WLM("preedit_string", "?sii"), FIRE_WLM("commit_string", "?s"), FIRE_WLM("delete_surrounding_text", "uu"), FIRE_WLM("done", "u")
};
static const FireWlIface fire_wlText = { "zwp_text_input_v3", 1, 8, fire_wlTextReq, 6, fire_wlTextEv };
static const FireWlMessage fire_wlShapeMgrReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("get_pointer", "no"), FIRE_WLM("get_tablet_tool_v2", "no") };
static const FireWlIface fire_wlShapeMgr = { "wp_cursor_shape_manager_v1", 1, 3, fire_wlShapeMgrReq, 0, NULL };
static const FireWlMessage fire_wlShapeReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("set_shape", "uu") };
static const FireWlIface fire_wlShape = { "wp_cursor_shape_device_v1", 1, 2, fire_wlShapeReq, 0, NULL };
static const FireWlMessage fire_wlBgMgrReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("get_background_effect", "no") };
static const FireWlMessage fire_wlBgMgrEv[] = { FIRE_WLM("capabilities", "u") };
static const FireWlIface fire_wlBgMgr = { "ext_background_effect_manager_v1", 1, 2, fire_wlBgMgrReq, 1, fire_wlBgMgrEv };
static const FireWlMessage fire_wlBgReq[] = { FIRE_WLM("destroy", ""), FIRE_WLM("set_blur_region", "?o") };
static const FireWlIface fire_wlBg = { "ext_background_effect_surface_v1", 1, 2, fire_wlBgReq, 0, NULL };
static const FireWlMessage fire_wlKdeBlurMgrReq[] = { FIRE_WLM("create", "no"), FIRE_WLM("unset", "o") };
static const FireWlIface fire_wlKdeBlurMgr = { "org_kde_kwin_blur_manager", 1, 2, fire_wlKdeBlurMgrReq, 0, NULL };
static const FireWlMessage fire_wlKdeBlurReq[] = { FIRE_WLM("commit", ""), FIRE_WLM("set_region", "?o"), FIRE_WLM("release", "") };
static const FireWlIface fire_wlKdeBlur = { "org_kde_kwin_blur", 1, 3, fire_wlKdeBlurReq, 0, NULL };

/* ---- libwayland-client, libxkbcommon, libwayland-cursor ---- */
typedef struct FireWlc {
    int tried, ok;
    void* lib;
    void* xkb;
    void* cursorLib;
    const FireWlIface *iRegistry, *iCompositor, *iSurface, *iRegion, *iShm, *iShmPool, *iBuffer, *iSeat, *iPointer, *iKeyboard, *iTouch, *iOutput, *iCallback, *iDdm, *iDevice, *iOffer, *iSource;
    void* (*connect)(const char*);
    void (*disconnect)(void*);
    int (*roundtrip)(void*);
    int (*dispatchPending)(void*);
    int (*flush)(void*);
    int (*getFd)(void*);
    int (*prepareRead)(void*);
    int (*readEvents)(void*);
    void (*cancelRead)(void*);
    int (*displayError)(void*);
    void* (*marshalConstructor)(void*, uint32_t, const void*, ...);
    void* (*marshalConstructorVersioned)(void*, uint32_t, const void*, uint32_t, ...);
    void (*marshal)(void*, uint32_t, ...);
    int (*addListener)(void*, FireWlVoid*, void*);
    void (*destroy)(void*);
    uint32_t (*getVersion)(void*);
    /* libxkbcommon */
    void* (*xkbContextNew)(int);
    void (*xkbContextUnref)(void*);
    void* (*xkbKeymapNewFromString)(void*, const char*, int, int);
    void (*xkbKeymapUnref)(void*);
    void* (*xkbStateNew)(void*);
    void (*xkbStateUnref)(void*);
    int (*xkbStateUpdateMask)(void*, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t);
    int (*xkbStateKeyGetUtf8)(void*, uint32_t, char*, size_t);
    uint32_t (*xkbStateKeyGetOneSym)(void*, uint32_t);
    uint32_t (*xkbStateKeyGetLayout)(void*, uint32_t);
    int (*xkbKeymapKeyGetSymsByLevel)(void*, uint32_t, uint32_t, uint32_t, const uint32_t**);
    uint32_t (*xkbKeysymToUtf32)(uint32_t);
    int (*xkbStateModNameIsActive)(void*, const char*, int);
    int (*xkbKeymapKeyRepeats)(void*, uint32_t);
    void* (*xkbComposeTableNewFromLocale)(void*, const char*, int);
    void (*xkbComposeTableUnref)(void*);
    void* (*xkbComposeStateNew)(void*, int);
    void (*xkbComposeStateUnref)(void*);
    int (*xkbComposeStateFeed)(void*, uint32_t);
    int (*xkbComposeStateGetStatus)(void*);
    int (*xkbComposeStateGetUtf8)(void*, char*, size_t);
    void (*xkbComposeStateReset)(void*);
    /* libwayland-cursor */
    void* (*cursorThemeLoad)(const char*, int, void*);
    void (*cursorThemeDestroy)(void*);
    void* (*cursorThemeGetCursor)(void*, const char*);
    void* (*cursorImageGetBuffer)(void*);
} FireWlc;

static int fire_dwl_sym(void* lib, const char* name, void* fn) {
    void* p = lib != NULL ? dlsym(lib, name) : NULL;
    memcpy(fn, &p, sizeof p);
    return p != NULL;
}
static void* fire_dwl_open2(const char* a, const char* b) {
    void* lib = dlopen(a, RTLD_LAZY | RTLD_LOCAL);
    return lib != NULL ? lib : dlopen(b, RTLD_LAZY | RTLD_LOCAL);
}
static const FireWlIface* fire_dwl_iface(void* lib, const char* name) { return (const FireWlIface*)dlsym(lib, name); }

static FireWlc* fire_dwl_lib(void) {
    static FireWlc w;
    static pthread_mutex_t guard = PTHREAD_MUTEX_INITIALIZER;
    pthread_mutex_lock(&guard);
    if (!w.tried) {
        void* l;
        w.tried = 1;
        w.lib = l = fire_dwl_open2("libwayland-client.so.0", "libwayland-client.so");
        w.ok = l != NULL
            && (w.iRegistry = fire_dwl_iface(l, "wl_registry_interface")) != NULL && (w.iCompositor = fire_dwl_iface(l, "wl_compositor_interface")) != NULL
            && (w.iSurface = fire_dwl_iface(l, "wl_surface_interface")) != NULL && (w.iRegion = fire_dwl_iface(l, "wl_region_interface")) != NULL
            && (w.iShm = fire_dwl_iface(l, "wl_shm_interface")) != NULL && (w.iShmPool = fire_dwl_iface(l, "wl_shm_pool_interface")) != NULL
            && (w.iBuffer = fire_dwl_iface(l, "wl_buffer_interface")) != NULL && (w.iSeat = fire_dwl_iface(l, "wl_seat_interface")) != NULL
            && (w.iPointer = fire_dwl_iface(l, "wl_pointer_interface")) != NULL && (w.iKeyboard = fire_dwl_iface(l, "wl_keyboard_interface")) != NULL
            && (w.iTouch = fire_dwl_iface(l, "wl_touch_interface")) != NULL && (w.iOutput = fire_dwl_iface(l, "wl_output_interface")) != NULL
            && (w.iCallback = fire_dwl_iface(l, "wl_callback_interface")) != NULL && (w.iDdm = fire_dwl_iface(l, "wl_data_device_manager_interface")) != NULL
            && (w.iDevice = fire_dwl_iface(l, "wl_data_device_interface")) != NULL && (w.iOffer = fire_dwl_iface(l, "wl_data_offer_interface")) != NULL
            && (w.iSource = fire_dwl_iface(l, "wl_data_source_interface")) != NULL
            && fire_dwl_sym(l, "wl_display_connect", &w.connect) && fire_dwl_sym(l, "wl_display_disconnect", &w.disconnect)
            && fire_dwl_sym(l, "wl_display_roundtrip", &w.roundtrip) && fire_dwl_sym(l, "wl_display_dispatch_pending", &w.dispatchPending)
            && fire_dwl_sym(l, "wl_display_flush", &w.flush) && fire_dwl_sym(l, "wl_display_get_fd", &w.getFd)
            && fire_dwl_sym(l, "wl_display_prepare_read", &w.prepareRead) && fire_dwl_sym(l, "wl_display_read_events", &w.readEvents)
            && fire_dwl_sym(l, "wl_display_cancel_read", &w.cancelRead) && fire_dwl_sym(l, "wl_display_get_error", &w.displayError)
            && fire_dwl_sym(l, "wl_proxy_marshal_constructor", &w.marshalConstructor) && fire_dwl_sym(l, "wl_proxy_marshal_constructor_versioned", &w.marshalConstructorVersioned)
            && fire_dwl_sym(l, "wl_proxy_marshal", &w.marshal) && fire_dwl_sym(l, "wl_proxy_add_listener", &w.addListener)
            && fire_dwl_sym(l, "wl_proxy_destroy", &w.destroy) && fire_dwl_sym(l, "wl_proxy_get_version", &w.getVersion);
        if (w.ok) {
            w.xkb = fire_dwl_open2("libxkbcommon.so.0", "libxkbcommon.so");
            if (!(fire_dwl_sym(w.xkb, "xkb_context_new", &w.xkbContextNew) && fire_dwl_sym(w.xkb, "xkb_context_unref", &w.xkbContextUnref)
                  && fire_dwl_sym(w.xkb, "xkb_keymap_new_from_string", &w.xkbKeymapNewFromString) && fire_dwl_sym(w.xkb, "xkb_keymap_unref", &w.xkbKeymapUnref)
                  && fire_dwl_sym(w.xkb, "xkb_state_new", &w.xkbStateNew) && fire_dwl_sym(w.xkb, "xkb_state_unref", &w.xkbStateUnref)
                  && fire_dwl_sym(w.xkb, "xkb_state_update_mask", &w.xkbStateUpdateMask) && fire_dwl_sym(w.xkb, "xkb_state_key_get_utf8", &w.xkbStateKeyGetUtf8)
                  && fire_dwl_sym(w.xkb, "xkb_state_key_get_one_sym", &w.xkbStateKeyGetOneSym) && fire_dwl_sym(w.xkb, "xkb_state_key_get_layout", &w.xkbStateKeyGetLayout)
                  && fire_dwl_sym(w.xkb, "xkb_keymap_key_get_syms_by_level", &w.xkbKeymapKeyGetSymsByLevel) && fire_dwl_sym(w.xkb, "xkb_keysym_to_utf32", &w.xkbKeysymToUtf32)
                  && fire_dwl_sym(w.xkb, "xkb_state_mod_name_is_active", &w.xkbStateModNameIsActive) && fire_dwl_sym(w.xkb, "xkb_keymap_key_repeats", &w.xkbKeymapKeyRepeats)))
                w.xkbContextNew = NULL;
            if (!(fire_dwl_sym(w.xkb, "xkb_compose_table_new_from_locale", &w.xkbComposeTableNewFromLocale) && fire_dwl_sym(w.xkb, "xkb_compose_table_unref", &w.xkbComposeTableUnref)
                  && fire_dwl_sym(w.xkb, "xkb_compose_state_new", &w.xkbComposeStateNew) && fire_dwl_sym(w.xkb, "xkb_compose_state_unref", &w.xkbComposeStateUnref)
                  && fire_dwl_sym(w.xkb, "xkb_compose_state_feed", &w.xkbComposeStateFeed) && fire_dwl_sym(w.xkb, "xkb_compose_state_get_status", &w.xkbComposeStateGetStatus)
                  && fire_dwl_sym(w.xkb, "xkb_compose_state_get_utf8", &w.xkbComposeStateGetUtf8) && fire_dwl_sym(w.xkb, "xkb_compose_state_reset", &w.xkbComposeStateReset)))
                w.xkbComposeTableNewFromLocale = NULL;
            w.cursorLib = fire_dwl_open2("libwayland-cursor.so.0", "libwayland-cursor.so");
            if (!(fire_dwl_sym(w.cursorLib, "wl_cursor_theme_load", &w.cursorThemeLoad) && fire_dwl_sym(w.cursorLib, "wl_cursor_theme_destroy", &w.cursorThemeDestroy)
                  && fire_dwl_sym(w.cursorLib, "wl_cursor_theme_get_cursor", &w.cursorThemeGetCursor) && fire_dwl_sym(w.cursorLib, "wl_cursor_image_get_buffer", &w.cursorImageGetBuffer)))
                w.cursorThemeLoad = NULL;
        }
    }
    pthread_mutex_unlock(&guard);
    return w.ok ? &w : NULL;
}

/* Can a window be opened on Wayland (libwayland-client is there and the compositor named by WAYLAND_DISPLAY answers)? */
static int fire_dwl_available(void) {
    FireWlc* l;
    void* d;
    const char* name = getenv("WAYLAND_DISPLAY");
    if (name == NULL || name[0] == 0 || (l = fire_dwl_lib()) == NULL) return 0;
    d = l->connect(NULL);
    if (d == NULL) return 0;
    l->disconnect(d);
    return 1;
}

/* (calls of requests: the opcode, then the arguments; a request that makes an object names its interface and version) */
#define FIRE_WL_REQ(l, proxy, op, ...) (l)->marshal((proxy), (uint32_t)(op), __VA_ARGS__)
#define FIRE_WL_REQ0(l, proxy, op) (l)->marshal((proxy), (uint32_t)(op))
static void* fire_dwl_new(FireWlc* l, void* proxy, uint32_t op, const FireWlIface* iface) { return l->marshalConstructor(proxy, op, iface, NULL); }
static void* fire_dwl_new1(FireWlc* l, void* proxy, uint32_t op, const FireWlIface* iface, void* arg) { return l->marshalConstructor(proxy, op, iface, NULL, arg); }
static void* fire_dwl_bind(FireWlc* l, void* registry, uint32_t name, const FireWlIface* iface, uint32_t version) {
    return l->marshalConstructorVersioned(registry, 0, iface, version, name, iface->name, version, NULL);
}
static void fire_dwl_destroyRequest(FireWlc* l, void* proxy, uint32_t op) { if (proxy != NULL) { FIRE_WL_REQ0(l, proxy, op); l->destroy(proxy); } }

static double fire_dwl_now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + (double)t.tv_nsec * 1e-9; }

/* ---- a window ---- */
#define FIRE_DWL_TITLE 30   /* the height of fire's own title bar (logical pixels) */
/* (`dirty`: what changed in the frames since this buffer was drawn last - x0, y0, x1, y1 of the framebuffer; dirtyAll: all of it, e.g. a new buffer) */
typedef struct FireDwlBuffer { void* buffer; uint32_t* data; size_t size; int width, height, busy; int dirty[4], dirtyAll; } FireDwlBuffer;
typedef struct FireDwlOffer { void* offer; char mimes[8][48]; int mimeCount; } FireDwlOffer;

typedef struct FireDwlWindow {
    int damage[4], damageAll;   /* what the next draw tells the compositor changed (x0, y0, x1, y1 of the framebuffer; damageAll: all of it) */
    FireWlc* l;
    void* d;
    void* registry;
    void *compositor, *shm, *seat, *wmBase, *decoMgr, *viewporter, *fracMgr, *textMgr, *shapeMgr, *ddm, *bgMgr, *kdeBlurMgr;
    uint32_t seatVersion, compositorVersion, ddmVersion;
    void *pointer, *keyboard, *touchDev, *shapeDevice, *dataDevice, *textInput;
    void *surface, *xdgSurface, *toplevel, *deco, *viewport, *frac, *bgEffect, *kdeBlur;
    void* outputs[8]; int outputScale[8]; int outputCount;
    void* entered[8]; int enteredCount;
    int intScale;            /* the scale of the outputs the window is on */
    int fracScale120;        /* the fractional scale (120 = 1), 0: none */
    int configured, closed, maximized, activated;
    int pendingW, pendingH;  /* the size of the last configure (0: the window decides) */
    int serverDecorations, csd;   /* the compositor draws the frame / the window draws its title bar */
    int winW, winH;          /* the framebuffer's part of the window (logical pixels) */
    int vsync, touchMouse, cursor, direct, backdrop;
    char* title;
    FireDwlBuffer buffers[2];
    void* frameCallback;
    int framePending;
    uint32_t* lastFrame; int lastW, lastH;   /* the last framebuffer, for a redraw of the title bar */
    /* the pointer */
    uint32_t enterSerial, pressSerial;
    int pointerInside;       /* over the window: 1 content, 2 title bar/edges */
    float px, py;            /* the position on the surface */
    float axisX, axisY; int discreteX, discreteY, axisSeen;
    void* cursorTheme; void* cursorSurface; int themeCursor;
    /* the keyboard */
    void *xkbContext, *keymap, *xkbState, *composeTable, *composeState;
    uint32_t keySerial;
    int mods;
    int repeatRate, repeatDelay;
    uint32_t repeatKey; double repeatNext; int repeating;
    int hasFocus;
    /* the input method */
    int imActive, imEnabled, imUsed;
    char* preedit; int preeditCursor; char* commit; int imChanged;
    int textX, textY, textW, textH;
    /* fingers */
    int touchMouseFinger;
    /* drops */
    FireDwlOffer offers[4];
    void* dragOffer; const char* dragMime; float dragX, dragY; uint32_t dragSerial;
    void* selectionOffer;
    /* the clipboard: what this program offers */
    void* selectionSource;
    pthread_mutex_t lock;
    FireDisplayQueue queue;
    int dark;                /* the look of the title bar */
} FireDwlWindow;

/* ---- the windows of this program: the clipboard uses the one that has the keyboard ---- */
static pthread_mutex_t fire_dwlWindowsLock = PTHREAD_MUTEX_INITIALIZER;
static FireDwlWindow* fire_dwlWindows[64];
static int fire_dwlWindowCount = 0;
static FireDwlWindow* fire_dwlFocused = NULL;

static void fire_dwl_threadDone(void) {}

static void fire_dwl_init(FireDwlWindow* w) {
    memset(w, 0, sizeof *w);
    w->vsync = 1; w->touchMouse = 1; w->cursor = -1; w->intScale = 1; w->touchMouseFinger = -1;
    w->repeatRate = 25; w->repeatDelay = 600;
    pthread_mutex_init(&w->lock, NULL);
}

static double fire_dwl_scaleOf(const FireDwlWindow* w) { return w->fracScale120 > 0 ? w->fracScale120 / 120.0 : (double)w->intScale; }
static int fire_dwl_titleH(const FireDwlWindow* w) { return w->csd ? FIRE_DWL_TITLE : 0; }

/* ---- the registry ---- */
static void fire_dwl_outputGeometry(void* data, void* o, int32_t x, int32_t y, int32_t pw, int32_t ph, int32_t sub, const char* make, const char* model, int32_t tr) {
    (void)data; (void)o; (void)x; (void)y; (void)pw; (void)ph; (void)sub; (void)make; (void)model; (void)tr;
}
static void fire_dwl_outputMode(void* data, void* o, uint32_t flags, int32_t width, int32_t height, int32_t refresh) { (void)data; (void)o; (void)flags; (void)width; (void)height; (void)refresh; }
static void fire_dwl_outputDone(void* data, void* o) { (void)data; (void)o; }
static void fire_dwl_updateIntScale(FireDwlWindow* w);
static void fire_dwl_outputScale(void* data, void* o, int32_t factor) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int i;
    for (i = 0; i < w->outputCount; i++) if (w->outputs[i] == o) w->outputScale[i] = factor > 0 ? factor : 1;
    fire_dwl_updateIntScale(w);
}
static const struct { void (*geometry)(void*, void*, int32_t, int32_t, int32_t, int32_t, int32_t, const char*, const char*, int32_t); void (*mode)(void*, void*, uint32_t, int32_t, int32_t, int32_t);
                      void (*done)(void*, void*); void (*scale)(void*, void*, int32_t); }
    fire_dwlOutputListener = { fire_dwl_outputGeometry, fire_dwl_outputMode, fire_dwl_outputDone, fire_dwl_outputScale };

static void fire_dwl_seatCaps(void* data, void* seat, uint32_t caps);
static void fire_dwl_seatName(void* data, void* seat, const char* name) { (void)data; (void)seat; (void)name; }
static const struct { void (*caps)(void*, void*, uint32_t); void (*name)(void*, void*, const char*); } fire_dwlSeatListener = { fire_dwl_seatCaps, fire_dwl_seatName };

static void fire_dwl_ping(void* data, void* base, uint32_t serial) { FireDwlWindow* w = (FireDwlWindow*)data; FIRE_WL_REQ(w->l, base, 3, serial); }
static const struct { void (*ping)(void*, void*, uint32_t); } fire_dwlWmBaseListener = { fire_dwl_ping };

static void fire_dwl_bgCaps(void* data, void* mgr, uint32_t flags) { (void)data; (void)mgr; (void)flags; }
static const struct { void (*caps)(void*, void*, uint32_t); } fire_dwlBgListener = { fire_dwl_bgCaps };

static uint32_t fire_dwl_min(uint32_t a, uint32_t b) { return a < b ? a : b; }
static void fire_dwl_global(void* data, void* registry, uint32_t name, const char* iface, uint32_t version) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    FireWlc* l = w->l;
    if (strcmp(iface, "wl_compositor") == 0) { w->compositorVersion = fire_dwl_min(version, 4); w->compositor = fire_dwl_bind(l, registry, name, l->iCompositor, w->compositorVersion); }
    else if (strcmp(iface, "wl_shm") == 0) w->shm = fire_dwl_bind(l, registry, name, l->iShm, 1);
    else if (strcmp(iface, "wl_seat") == 0 && w->seat == NULL) {
        w->seatVersion = fire_dwl_min(version, 5);
        w->seat = fire_dwl_bind(l, registry, name, l->iSeat, w->seatVersion);
        l->addListener(w->seat, (FireWlVoid*)(void*)&fire_dwlSeatListener, w);
    }
    else if (strcmp(iface, "xdg_wm_base") == 0) { w->wmBase = fire_dwl_bind(l, registry, name, &fire_wlXdgWmBase, fire_dwl_min(version, 3)); l->addListener(w->wmBase, (FireWlVoid*)(void*)&fire_dwlWmBaseListener, w); }
    else if (strcmp(iface, "zxdg_decoration_manager_v1") == 0) w->decoMgr = fire_dwl_bind(l, registry, name, &fire_wlDecoMgr, 1);
    else if (strcmp(iface, "wp_viewporter") == 0) w->viewporter = fire_dwl_bind(l, registry, name, &fire_wlViewporter, 1);
    else if (strcmp(iface, "wp_fractional_scale_manager_v1") == 0) w->fracMgr = fire_dwl_bind(l, registry, name, &fire_wlFracMgr, 1);
    else if (strcmp(iface, "zwp_text_input_manager_v3") == 0) w->textMgr = fire_dwl_bind(l, registry, name, &fire_wlTextMgr, 1);
    else if (strcmp(iface, "wp_cursor_shape_manager_v1") == 0) w->shapeMgr = fire_dwl_bind(l, registry, name, &fire_wlShapeMgr, 1);
    else if (strcmp(iface, "wl_data_device_manager") == 0) { w->ddmVersion = fire_dwl_min(version, 3); w->ddm = fire_dwl_bind(l, registry, name, l->iDdm, w->ddmVersion); }
    else if (strcmp(iface, "ext_background_effect_manager_v1") == 0) { w->bgMgr = fire_dwl_bind(l, registry, name, &fire_wlBgMgr, 1); l->addListener(w->bgMgr, (FireWlVoid*)(void*)&fire_dwlBgListener, w); }
    else if (strcmp(iface, "org_kde_kwin_blur_manager") == 0) w->kdeBlurMgr = fire_dwl_bind(l, registry, name, &fire_wlKdeBlurMgr, 1);
    else if (strcmp(iface, "wl_output") == 0 && w->outputCount < 8) {
        void* o = fire_dwl_bind(l, registry, name, l->iOutput, fire_dwl_min(version, 2));
        w->outputs[w->outputCount] = o; w->outputScale[w->outputCount] = 1; w->outputCount++;
        l->addListener(o, (FireWlVoid*)(void*)&fire_dwlOutputListener, w);
    }
}
static void fire_dwl_globalRemove(void* data, void* registry, uint32_t name) { (void)data; (void)registry; (void)name; }
static const struct { void (*global)(void*, void*, uint32_t, const char*, uint32_t); void (*remove)(void*, void*, uint32_t); } fire_dwlRegistryListener = { fire_dwl_global, fire_dwl_globalRemove };

/* ---- the surface and the scale ---- */
static void fire_dwl_updateIntScale(FireDwlWindow* w) {
    int i, k, best = 1;
    double before = fire_dwl_scaleOf(w);
    for (k = 0; k < w->enteredCount; k++)
        for (i = 0; i < w->outputCount; i++) if (w->outputs[i] == w->entered[k] && w->outputScale[i] > best) best = w->outputScale[i];
    if (w->enteredCount == 0) for (i = 0; i < w->outputCount; i++) if (w->outputScale[i] > best) best = w->outputScale[i];
    w->intScale = best;
    if (fire_dwl_scaleOf(w) != before && w->configured) fire_display_queueScale(&w->queue, fire_dwl_scaleOf(w));
}
static void fire_dwl_surfaceEnter(void* data, void* s, void* output) { FireDwlWindow* w = (FireDwlWindow*)data; (void)s; if (w->enteredCount < 8) w->entered[w->enteredCount++] = output; fire_dwl_updateIntScale(w); }
static void fire_dwl_surfaceLeave(void* data, void* s, void* output) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int k;
    (void)s;
    for (k = 0; k < w->enteredCount; k++) if (w->entered[k] == output) { w->entered[k] = w->entered[--w->enteredCount]; break; }
    fire_dwl_updateIntScale(w);
}
static const struct { void (*enter)(void*, void*, void*); void (*leave)(void*, void*, void*); } fire_dwlSurfaceListener = { fire_dwl_surfaceEnter, fire_dwl_surfaceLeave };
static void fire_dwl_fracScale(void* data, void* f, uint32_t scale) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    double before = fire_dwl_scaleOf(w);
    (void)f;
    w->fracScale120 = (int)scale;
    if (fire_dwl_scaleOf(w) != before && w->configured) fire_display_queueScale(&w->queue, fire_dwl_scaleOf(w));
}
static const struct { void (*preferred)(void*, void*, uint32_t); } fire_dwlFracListener = { fire_dwl_fracScale };

/* ---- xdg-shell ---- */
static void fire_dwl_xdgConfigure(void* data, void* xs, uint32_t serial) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    FIRE_WL_REQ(w->l, xs, 4, serial);   /* ack_configure */
    if (w->pendingW > 0 && w->pendingH > 0) {
        int h = w->pendingH - fire_dwl_titleH(w);
        if (h < 1) h = 1;
        if (w->pendingW != w->winW || h != w->winH) {
            w->winW = w->pendingW; w->winH = h;
            if (w->configured) fire_display_queueResize(&w->queue, w->winW, w->winH);
        }
    }
    w->configured = 1;
}
static const struct { void (*configure)(void*, void*, uint32_t); } fire_dwlXdgSurfaceListener = { fire_dwl_xdgConfigure };
static void fire_dwl_toplevelConfigure(void* data, void* t, int32_t width, int32_t height, FireWlArray* states) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    size_t i;
    (void)t;
    w->pendingW = width; w->pendingH = height;
    w->maximized = 0; w->activated = 0;
    for (i = 0; states != NULL && i + 4 <= states->size; i += 4) {
        uint32_t s;
        memcpy(&s, (char*)states->data + i, 4);
        if (s == 1) w->maximized = 1;
        if (s == 4) w->activated = 1;
    }
}
static void fire_dwl_toplevelClose(void* data, void* t) { FireDwlWindow* w = (FireDwlWindow*)data; (void)t; fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE_REQUEST); }
static const struct { void (*configure)(void*, void*, int32_t, int32_t, FireWlArray*); void (*close)(void*, void*); } fire_dwlToplevelListener = { fire_dwl_toplevelConfigure, fire_dwl_toplevelClose };
/* (FIRE_WAYLAND_CSD=0: no title bar of fire's own - the tests, on a compositor that draws none) */
static int fire_dwl_ownTitleBar(void) { const char* e = getenv("FIRE_WAYLAND_CSD"); return e == NULL || strcmp(e, "0") != 0; }
static void fire_dwl_decoConfigure(void* data, void* d, uint32_t mode) { FireDwlWindow* w = (FireDwlWindow*)data; (void)d; w->serverDecorations = mode == 2; w->csd = mode != 2 && fire_dwl_ownTitleBar(); }
static const struct { void (*configure)(void*, void*, uint32_t); } fire_dwlDecoListener = { fire_dwl_decoConfigure };

/* ---- the cursor ---- */
static void fire_dwl_applyCursor(FireDwlWindow* w, int c) {
    /* cursor-shape-v1 shapes for the HAL's cursors */
    static const uint32_t shapes[FIRE_CURSOR_COUNT] = { 1, 9, 6, 8, 5, 29, 28, 26, 27, 13, 15, 4 };
    static const char* const names[FIRE_CURSOR_COUNT] = { "left_ptr", "xterm", "watch", "crosshair", "left_ptr_watch", "bottom_right_corner", "bottom_left_corner", "sb_h_double_arrow", "sb_v_double_arrow", "fleur", "crossed_circle", "hand2" };
    FireWlc* l = w->l;
    if (w->pointer == NULL || w->enterSerial == 0) return;
    if (w->shapeMgr != NULL) {
        if (w->shapeDevice == NULL) w->shapeDevice = fire_dwl_new1(l, w->shapeMgr, 1, &fire_wlShape, w->pointer);
        if (w->shapeDevice != NULL) { FIRE_WL_REQ(l, w->shapeDevice, 1, w->enterSerial, shapes[c]); return; }
    }
    if (l->cursorThemeLoad != NULL && w->shm != NULL) {
        int size = 24 * (int)(fire_dwl_scaleOf(w) + 0.5);
        if (w->cursorTheme == NULL) w->cursorTheme = l->cursorThemeLoad(getenv("XCURSOR_THEME"), size, w->shm);
        if (w->cursorTheme != NULL) {
            /* struct wl_cursor { unsigned int image_count; struct wl_cursor_image** images; char* name; }; struct wl_cursor_image { uint32_t width, height, hotspot_x, hotspot_y, delay; } */
            void* cursor = l->cursorThemeGetCursor(w->cursorTheme, names[c]);
            if (cursor == NULL) cursor = l->cursorThemeGetCursor(w->cursorTheme, "left_ptr");
            if (cursor != NULL && *(unsigned int*)cursor > 0) {
                void* image = (*(void***)((char*)cursor + sizeof(void*)))[0];
                uint32_t* dims = (uint32_t*)image;
                void* buffer = l->cursorImageGetBuffer(image);
                int s = (int)(fire_dwl_scaleOf(w) + 0.5);
                if (s < 1) s = 1;
                if (w->cursorSurface == NULL) w->cursorSurface = fire_dwl_new(l, w->compositor, 0, l->iSurface);
                if (buffer != NULL && w->cursorSurface != NULL) {
                    if (w->compositorVersion >= 3) FIRE_WL_REQ(l, w->cursorSurface, 8, s);   /* set_buffer_scale */
                    FIRE_WL_REQ(l, w->cursorSurface, 1, buffer, 0, 0);                     /* attach */
                    FIRE_WL_REQ(l, w->cursorSurface, 2, 0, 0, (int32_t)dims[0], (int32_t)dims[1]);   /* damage */
                    FIRE_WL_REQ0(l, w->cursorSurface, 6);                                 /* commit */
                    FIRE_WL_REQ(l, w->pointer, 0, w->enterSerial, w->cursorSurface, (int32_t)dims[2] / s, (int32_t)dims[3] / s);   /* set_cursor */
                }
            }
        }
    }
}

/* ---- fire's own title bar (when the compositor draws no frame) ---- */
enum { FIRE_DWL_HIT_NONE = 0, FIRE_DWL_HIT_TITLE = 1, FIRE_DWL_HIT_CLOSE = 2, FIRE_DWL_HIT_MAX = 3, FIRE_DWL_HIT_MIN = 4, FIRE_DWL_HIT_EDGE = 5 };
/* What is at (x, y) of the surface (logical): the content, the title bar, a button, an edge (`edges`: the resize edges of xdg-shell). */
static int fire_dwl_hit(const FireDwlWindow* w, float x, float y, uint32_t* edges) {
    int T = fire_dwl_titleH(w), W = w->winW, H = w->winH + T, e = 0;
    *edges = 0;
    if (!w->csd) return FIRE_DWL_HIT_NONE;
    if (!w->maximized) {
        if (x < 4) e |= 4; else if (x >= W - 4) e |= 8;
        if (y < 4) e |= 1; else if (y >= H - 4) e |= 2;
        if (e) { *edges = (uint32_t)e; return FIRE_DWL_HIT_EDGE; }
    }
    if (y >= T) return FIRE_DWL_HIT_NONE;
    if (x >= W - 46) return FIRE_DWL_HIT_CLOSE;
    if (x >= W - 92) return FIRE_DWL_HIT_MAX;
    if (x >= W - 138) return FIRE_DWL_HIT_MIN;
    return FIRE_DWL_HIT_TITLE;
}

static void fire_dwl_fill(uint32_t* px, int stride, int x0, int y0, int x1, int y1, uint32_t argb, int W, int H) {
    int x, y;
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1 > W) x1 = W;
    if (y1 > H) y1 = H;
    for (y = y0; y < y1; y++) for (x = x0; x < x1; x++) px[(size_t)y * (size_t)stride + (size_t)x] = argb;
}
/* Draws the title bar into the top `th` rows (pixels) of a buffer `bw` wide, at scale `s`. */
static void fire_dwl_drawTitle(FireDwlWindow* w, uint32_t* px, int bw, int th, double s) {
    uint32_t bg = w->dark ? (w->activated ? 0xFF2B2B2Bu : 0xFF242424u) : (w->activated ? 0xFFEBEBEBu : 0xFFF4F4F4u);
    uint32_t fg = w->dark ? (w->activated ? 0xFFE8E8E8u : 0xFF8A8A8Au) : (w->activated ? 0xFF1E1E1Eu : 0xFF8A8A8Au);
    uint32_t line = w->dark ? 0xFF3A3A3Au : 0xFFD6D6D6u;
    int bwid = (int)(46 * s), cy = th / 2, k, i, len;
    fire_dwl_fill(px, bw, 0, 0, bw, th, bg, bw, th);
    fire_dwl_fill(px, bw, 0, th - 1, bw, th, line, bw, th);
    /* the buttons: close (x), maximize (a square), minimize (a line) - thin strokes, no circles */
    for (k = 0; k < 3; k++) {
        int cx = bw - bwid * k - bwid / 2, r = (int)(5 * s);
        if (k == 0) for (i = -r; i <= r; i++) {
            fire_dwl_fill(px, bw, cx + i, cy + i, cx + i + (int)(s + 0.5), cy + i + (int)(s + 0.5), fg, bw, th);
            fire_dwl_fill(px, bw, cx + i, cy - i, cx + i + (int)(s + 0.5), cy - i + (int)(s + 0.5), fg, bw, th);
        } else if (k == 1) {
            int t = (int)(s + 0.5);
            fire_dwl_fill(px, bw, cx - r, cy - r, cx + r, cy - r + t, fg, bw, th);
            fire_dwl_fill(px, bw, cx - r, cy + r - t, cx + r, cy + r, fg, bw, th);
            fire_dwl_fill(px, bw, cx - r, cy - r, cx - r + t, cy + r, fg, bw, th);
            fire_dwl_fill(px, bw, cx + r - t, cy - r, cx + r, cy + r, fg, bw, th);
        } else fire_dwl_fill(px, bw, cx - r, cy, cx + r, cy + (int)(s + 0.5), fg, bw, th);
    }
#ifdef FIRE_GFX_FONT_H
    /* the title with the built-in 8x14 font of the graphics bridge (code page 437; letters beyond ASCII as '?') */
    if (w->title != NULL) {
        int scale = s >= 1.5 ? (int)(s + 0.25) : 1, gw = 8 * scale, gh = 14 * scale, x0, y0, n = 0, maxChars = (bw - 3 * bwid - 24) / gw;
        const unsigned char* t = (const unsigned char*)w->title;
        unsigned char shown[256];
        while (*t && n < 255 && n < maxChars) { unsigned char c = *t++; if (c >= 0x80) { while ((*t & 0xC0) == 0x80) t++; c = '?'; } shown[n++] = c; }
        len = n;
        x0 = (bw - len * gw) / 2; if (x0 < 12) x0 = 12;
        y0 = (th - gh) / 2;
        for (i = 0; i < len; i++) {
            int gy, gx;
            for (gy = 0; gy < 14; gy++) {
                uint8_t bits = g_fireGfxGlyphs8x14[shown[i]][gy];
                for (gx = 0; gx < 8; gx++) if (bits & (0x80 >> gx)) fire_dwl_fill(px, bw, x0 + i * gw + gx * scale, y0 + gy * scale, x0 + i * gw + (gx + 1) * scale, y0 + (gy + 1) * scale, fg, bw, th);
            }
        }
    }
#else
    (void)len;
#endif
}

/* ---- the pointer ---- */
static void fire_dwl_pointerEnter(void* data, void* p, uint32_t serial, void* surface, int32_t x, int32_t y) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)p;
    if (surface != w->surface) return;
    w->enterSerial = serial;
    w->px = (float)x / 256.0f; w->py = (float)y / 256.0f;
    w->pointerInside = 1;
    fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_ENTER);
    fire_dwl_applyCursor(w, w->cursor >= 0 ? w->cursor : 0);
}
static void fire_dwl_pointerLeave(void* data, void* p, uint32_t serial, void* surface) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)p; (void)serial;
    if (surface != w->surface) return;
    w->pointerInside = 0;
    fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_LEAVE);
}
static void fire_dwl_pointerMotion(void* data, void* p, uint32_t time, int32_t x, int32_t y) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    uint32_t edges;
    int hit;
    (void)p; (void)time;
    w->px = (float)x / 256.0f; w->py = (float)y / 256.0f;
    hit = fire_dwl_hit(w, w->px, w->py, &edges);
    if (hit == FIRE_DWL_HIT_NONE) {
        if (w->pointerInside == 2) { w->pointerInside = 1; fire_dwl_applyCursor(w, w->cursor >= 0 ? w->cursor : 0); }
        fire_display_queueMotion(&w->queue, w->px, w->py - (float)fire_dwl_titleH(w));
    } else {
        int c = 0;
        if (hit == FIRE_DWL_HIT_EDGE) c = edges == 5 || edges == 10 ? FIRE_CURSOR_NWSE : edges == 6 || edges == 9 ? FIRE_CURSOR_NESW : (edges & 3) ? FIRE_CURSOR_NS : FIRE_CURSOR_EW;
        w->pointerInside = 2;
        fire_dwl_applyCursor(w, c);
    }
}
static void fire_dwl_pointerButton(void* data, void* p, uint32_t serial, uint32_t time, uint32_t button, uint32_t state) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int b = button == 0x110 ? FIRE_BUTTON_LEFT : button == 0x111 ? FIRE_BUTTON_RIGHT : button == 0x112 ? FIRE_BUTTON_MIDDLE : button == 0x113 ? FIRE_BUTTON_X1 : button == 0x114 ? FIRE_BUTTON_X2 : 0;
    uint32_t edges;
    int hit = fire_dwl_hit(w, w->px, w->py, &edges);
    (void)p; (void)time;
    if (state == 1) w->pressSerial = serial;
    if (hit != FIRE_DWL_HIT_NONE && !(w->queue.buttons & 1)) {
        /* the title bar: move, maximize with a double click; the buttons on release */
        static double lastClick = 0;
        if (state == 1 && b == FIRE_BUTTON_LEFT) {
            if (hit == FIRE_DWL_HIT_EDGE) FIRE_WL_REQ(w->l, w->toplevel, 6, w->seat, serial, edges);
            else if (hit == FIRE_DWL_HIT_TITLE) {
                double now = fire_dwl_now();
                if (now - lastClick < 0.4) { FIRE_WL_REQ0(w->l, w->toplevel, w->maximized ? 10 : 9); lastClick = 0; }
                else { lastClick = now; FIRE_WL_REQ(w->l, w->toplevel, 5, w->seat, serial); }
            }
        } else if (state == 0 && b == FIRE_BUTTON_LEFT) {
            if (hit == FIRE_DWL_HIT_CLOSE) fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE_REQUEST);
            else if (hit == FIRE_DWL_HIT_MAX) FIRE_WL_REQ0(w->l, w->toplevel, w->maximized ? 10 : 9);
            else if (hit == FIRE_DWL_HIT_MIN) FIRE_WL_REQ0(w->l, w->toplevel, 13);
        } else if (state == 1 && b == FIRE_BUTTON_RIGHT && hit == FIRE_DWL_HIT_TITLE) FIRE_WL_REQ(w->l, w->toplevel, 4, w->seat, serial, (int32_t)w->px, (int32_t)w->py);
        return;
    }
    if (b) fire_display_queueButton(&w->queue, state == 1, b, w->px, w->py - (float)fire_dwl_titleH(w));
}
static void fire_dwl_pointerAxis(void* data, void* p, uint32_t time, uint32_t axis, int32_t value) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)p; (void)time;
    if (axis == 0) w->axisY += (float)value / 256.0f; else w->axisX += (float)value / 256.0f;
    w->axisSeen = 1;
    if (w->seatVersion < 5) {
        fire_display_queueScroll(&w->queue, w->axisX / 10.0f, -w->axisY / 10.0f, w->px, w->py - (float)fire_dwl_titleH(w));
        w->axisX = w->axisY = 0; w->axisSeen = 0;
    }
}
static void fire_dwl_pointerFrame(void* data, void* p) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)p;
    if (w->axisSeen) {
        float dx = w->discreteX != 0 ? (float)w->discreteX : w->axisX / 10.0f, dy = w->discreteY != 0 ? (float)w->discreteY : w->axisY / 10.0f;
        if (w->pointerInside == 1) fire_display_queueScroll(&w->queue, dx, -dy, w->px, w->py - (float)fire_dwl_titleH(w));
    }
    w->axisX = w->axisY = 0; w->discreteX = w->discreteY = 0; w->axisSeen = 0;
}
static void fire_dwl_pointerAxisSource(void* data, void* p, uint32_t source) { (void)data; (void)p; (void)source; }
static void fire_dwl_pointerAxisStop(void* data, void* p, uint32_t time, uint32_t axis) { (void)data; (void)p; (void)time; (void)axis; }
static void fire_dwl_pointerAxisDiscrete(void* data, void* p, uint32_t axis, int32_t discrete) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)p;
    if (axis == 0) w->discreteY += discrete; else w->discreteX += discrete;
    w->axisSeen = 1;
}
static const struct {
    void (*enter)(void*, void*, uint32_t, void*, int32_t, int32_t); void (*leave)(void*, void*, uint32_t, void*); void (*motion)(void*, void*, uint32_t, int32_t, int32_t);
    void (*button)(void*, void*, uint32_t, uint32_t, uint32_t, uint32_t); void (*axis)(void*, void*, uint32_t, uint32_t, int32_t); void (*frame)(void*, void*);
    void (*axisSource)(void*, void*, uint32_t); void (*axisStop)(void*, void*, uint32_t, uint32_t); void (*axisDiscrete)(void*, void*, uint32_t, int32_t);
} fire_dwlPointerListener = { fire_dwl_pointerEnter, fire_dwl_pointerLeave, fire_dwl_pointerMotion, fire_dwl_pointerButton, fire_dwl_pointerAxis, fire_dwl_pointerFrame,
                              fire_dwl_pointerAxisSource, fire_dwl_pointerAxisStop, fire_dwl_pointerAxisDiscrete };

/* ---- the keyboard ---- */
static void fire_dwl_keymap(void* data, void* k, uint32_t format, int32_t fd, uint32_t size) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    FireWlc* l = w->l;
    char* text;
    (void)k;
    if (format != 1 /* xkb_v1 */ || l->xkbContextNew == NULL) { close(fd); return; }
    text = (char*)mmap(NULL, size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (text == MAP_FAILED) return;
    if (w->xkbContext == NULL) w->xkbContext = l->xkbContextNew(0);
    if (w->xkbState) { l->xkbStateUnref(w->xkbState); w->xkbState = NULL; }
    if (w->keymap) { l->xkbKeymapUnref(w->keymap); w->keymap = NULL; }
    if (w->xkbContext != NULL) w->keymap = l->xkbKeymapNewFromString(w->xkbContext, text, 1 /* XKB_KEYMAP_FORMAT_TEXT_V1 */, 0);
    munmap(text, size);
    if (w->keymap != NULL) w->xkbState = l->xkbStateNew(w->keymap);
    if (w->composeTable == NULL && l->xkbComposeTableNewFromLocale != NULL && w->xkbContext != NULL) {
        const char* locale = getenv("LC_ALL");
        if (locale == NULL || !*locale) locale = getenv("LC_CTYPE");
        if (locale == NULL || !*locale) locale = getenv("LANG");
        if (locale == NULL || !*locale) locale = "C";
        w->composeTable = l->xkbComposeTableNewFromLocale(w->xkbContext, locale, 0);
        if (w->composeTable != NULL) w->composeState = l->xkbComposeStateNew(w->composeTable, 0);
    }
}
static void fire_dwl_keyEnter(void* data, void* k, uint32_t serial, void* surface, FireWlArray* keys) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)k; (void)keys;
    if (surface != w->surface) return;
    w->keySerial = serial;
    w->hasFocus = 1;
    pthread_mutex_lock(&fire_dwlWindowsLock);
    fire_dwlFocused = w;
    pthread_mutex_unlock(&fire_dwlWindowsLock);
    fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_GAINED);
}
static void fire_dwl_keyLeave(void* data, void* k, uint32_t serial, void* surface) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)k; (void)serial;
    if (surface != w->surface) return;
    w->hasFocus = 0;
    w->repeating = 0;
    w->mods = 0;
    fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_LOST);
}
static int fire_dwl_modsNow(FireDwlWindow* w) {
    FireWlc* l = w->l;
    int m = w->mods;
    if (w->xkbState == NULL) return m;
    if (!l->xkbStateModNameIsActive(w->xkbState, "Shift", 1 /* XKB_STATE_MODS_EFFECTIVE */)) m &= ~(FIRE_MOD_LSHIFT | FIRE_MOD_RSHIFT); else if (!(m & (FIRE_MOD_LSHIFT | FIRE_MOD_RSHIFT))) m |= FIRE_MOD_LSHIFT;
    if (!l->xkbStateModNameIsActive(w->xkbState, "Control", 1)) m &= ~(FIRE_MOD_LCTRL | FIRE_MOD_RCTRL); else if (!(m & (FIRE_MOD_LCTRL | FIRE_MOD_RCTRL))) m |= FIRE_MOD_LCTRL;
    if (!l->xkbStateModNameIsActive(w->xkbState, "Mod1", 1)) m &= ~(FIRE_MOD_LALT | FIRE_MOD_RALT); else if (!(m & (FIRE_MOD_LALT | FIRE_MOD_RALT))) m |= FIRE_MOD_LALT;
    if (!l->xkbStateModNameIsActive(w->xkbState, "Mod4", 1)) m &= ~(FIRE_MOD_LGUI | FIRE_MOD_RGUI); else if (!(m & (FIRE_MOD_LGUI | FIRE_MOD_RGUI))) m |= FIRE_MOD_LGUI;
    if (l->xkbStateModNameIsActive(w->xkbState, "Lock", 1)) m |= FIRE_MOD_CAPS;
    if (l->xkbStateModNameIsActive(w->xkbState, "Mod2", 1)) m |= FIRE_MOD_NUM;
    if (l->xkbStateModNameIsActive(w->xkbState, "Mod5", 1)) m |= FIRE_MOD_MODE;
    return m;
}
/* One press (or repeat) of the evdev key `code`: the key event and its text. */
static void fire_dwl_keyPress(FireDwlWindow* w, uint32_t code, int down, int repeat) {
    FireWlc* l = w->l;
    int sc = fire_key_fromEvdev(code), bit = fire_key_modBit(sc), mods;
    uint32_t unshifted = 0;
    if (!repeat && bit) { if (down) w->mods |= bit; else w->mods &= ~bit; }
    mods = fire_dwl_modsNow(w);
    if (bit) { if (down) mods |= bit; else mods &= ~bit; }
    if (w->keymap != NULL) {
        const uint32_t* syms = NULL;
        uint32_t layout = l->xkbStateKeyGetLayout(w->xkbState, code + 8);
        if (l->xkbKeymapKeyGetSymsByLevel(w->keymap, code + 8, layout == 0xFFFFFFFFu ? 0 : layout, 0, &syms) > 0 && syms != NULL) unshifted = l->xkbKeysymToUtf32(syms[0]);
    }
    fire_display_queueKey(&w->queue, down, fire_key_code(sc, unshifted), sc, mods, repeat);
    if (!down || w->xkbState == NULL) return;
    /* the text: through the compose tables (dead keys), else what the keymap gives - unless the input method of the compositor types for this window */
    if (w->imActive && w->imUsed) return;
    {
        char buf[64];
        int n = 0;
        uint32_t sym = l->xkbStateKeyGetOneSym(w->xkbState, code + 8);
        if (w->composeState != NULL && l->xkbComposeStateFeed(w->composeState, sym) == 1 /* ACCEPTED */) {
            int status = l->xkbComposeStateGetStatus(w->composeState);
            if (status == 1 /* COMPOSING */) return;
            if (status == 2 /* COMPOSED */) { n = l->xkbComposeStateGetUtf8(w->composeState, buf, sizeof buf); l->xkbComposeStateReset(w->composeState); }
            else if (status == 3 /* CANCELLED */) { l->xkbComposeStateReset(w->composeState); return; }
            else n = l->xkbStateKeyGetUtf8(w->xkbState, code + 8, buf, sizeof buf);
        } else n = l->xkbStateKeyGetUtf8(w->xkbState, code + 8, buf, sizeof buf);
        if (n > 0 && n < (int)sizeof buf && !(mods & (FIRE_MOD_LCTRL | FIRE_MOD_RCTRL))) fire_display_queueText(&w->queue, buf, (size_t)n);
    }
}
static void fire_dwl_key(void* data, void* k, uint32_t serial, uint32_t time, uint32_t key, uint32_t state) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)k; (void)time;
    w->keySerial = serial;
    fire_dwl_keyPress(w, key, state == 1, 0);
    if (state == 1 && w->repeatRate > 0 && w->keymap != NULL && w->l->xkbKeymapKeyRepeats(w->keymap, key + 8)) {
        w->repeating = 1; w->repeatKey = key; w->repeatNext = fire_dwl_now() + w->repeatDelay / 1000.0;
    } else if (state == 0 && w->repeating && w->repeatKey == key) w->repeating = 0;
}
static void fire_dwl_modifiers(void* data, void* k, uint32_t serial, uint32_t depressed, uint32_t latched, uint32_t locked, uint32_t group) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)k; (void)serial;
    if (w->xkbState != NULL) w->l->xkbStateUpdateMask(w->xkbState, depressed, latched, locked, 0, 0, group);
}
static void fire_dwl_repeatInfo(void* data, void* k, int32_t rate, int32_t delay) { FireDwlWindow* w = (FireDwlWindow*)data; (void)k; w->repeatRate = rate; w->repeatDelay = delay; }
static const struct {
    void (*keymap)(void*, void*, uint32_t, int32_t, uint32_t); void (*enter)(void*, void*, uint32_t, void*, FireWlArray*); void (*leave)(void*, void*, uint32_t, void*);
    void (*key)(void*, void*, uint32_t, uint32_t, uint32_t, uint32_t); void (*modifiers)(void*, void*, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t); void (*repeat)(void*, void*, int32_t, int32_t);
} fire_dwlKeyboardListener = { fire_dwl_keymap, fire_dwl_keyEnter, fire_dwl_keyLeave, fire_dwl_key, fire_dwl_modifiers, fire_dwl_repeatInfo };

/* ---- fingers ---- */
static void fire_dwl_touchAt(FireDwlWindow* w, int type, int32_t id, float x, float y) {
    y -= (float)fire_dwl_titleH(w);
    fire_display_queueTouch(&w->queue, type, id, x, y, type == FIRE_EV_TOUCH_UP ? 0.0f : 1.0f);
    if (!w->touchMouse) return;
    if (type == FIRE_EV_TOUCH_DOWN && w->touchMouseFinger < 0) {
        w->touchMouseFinger = id;
        fire_display_queueMotion(&w->queue, x, y);
        fire_display_queueButton(&w->queue, 1, FIRE_BUTTON_LEFT, x, y);
    } else if (id == w->touchMouseFinger) {
        fire_display_queueMotion(&w->queue, x, y);
        if (type == FIRE_EV_TOUCH_UP) { fire_display_queueButton(&w->queue, 0, FIRE_BUTTON_LEFT, x, y); w->touchMouseFinger = -1; }
    }
}
typedef struct FireDwlFinger { int32_t id; float x, y; } FireDwlFinger;
static FireDwlFinger fire_dwlFingers[10];
static void fire_dwl_touchDown(void* data, void* t, uint32_t serial, uint32_t time, void* surface, int32_t id, int32_t x, int32_t y) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int k;
    (void)t; (void)serial; (void)time;
    if (surface != w->surface) return;
    for (k = 0; k < 10; k++) if (fire_dwlFingers[k].id == 0) { fire_dwlFingers[k].id = id + 1; fire_dwlFingers[k].x = (float)x / 256.0f; fire_dwlFingers[k].y = (float)y / 256.0f; break; }
    fire_dwl_touchAt(w, FIRE_EV_TOUCH_DOWN, id, (float)x / 256.0f, (float)y / 256.0f);
}
static void fire_dwl_touchUp(void* data, void* t, uint32_t serial, uint32_t time, int32_t id) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int k;
    (void)t; (void)serial; (void)time;
    for (k = 0; k < 10; k++) if (fire_dwlFingers[k].id == id + 1) { fire_dwl_touchAt(w, FIRE_EV_TOUCH_UP, id, fire_dwlFingers[k].x, fire_dwlFingers[k].y); fire_dwlFingers[k].id = 0; }
}
static void fire_dwl_touchMotion(void* data, void* t, uint32_t time, int32_t id, int32_t x, int32_t y) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int k;
    (void)t; (void)time;
    for (k = 0; k < 10; k++) if (fire_dwlFingers[k].id == id + 1) { fire_dwlFingers[k].x = (float)x / 256.0f; fire_dwlFingers[k].y = (float)y / 256.0f; }
    fire_dwl_touchAt(w, FIRE_EV_TOUCH_MOVE, id, (float)x / 256.0f, (float)y / 256.0f);
}
static void fire_dwl_touchFrame(void* data, void* t) { (void)data; (void)t; }
static void fire_dwl_touchCancel(void* data, void* t) { (void)data; (void)t; memset(fire_dwlFingers, 0, sizeof fire_dwlFingers); }
static const struct {
    void (*down)(void*, void*, uint32_t, uint32_t, void*, int32_t, int32_t, int32_t); void (*up)(void*, void*, uint32_t, uint32_t, int32_t); void (*motion)(void*, void*, uint32_t, int32_t, int32_t, int32_t);
    void (*frame)(void*, void*); void (*cancel)(void*, void*);
} fire_dwlTouchListener = { fire_dwl_touchDown, fire_dwl_touchUp, fire_dwl_touchMotion, fire_dwl_touchFrame, fire_dwl_touchCancel };

static void fire_dwl_seatCaps(void* data, void* seat, uint32_t caps) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    FireWlc* l = w->l;
    if ((caps & 1) && w->pointer == NULL) { w->pointer = fire_dwl_new(l, seat, 0, l->iPointer); l->addListener(w->pointer, (FireWlVoid*)(void*)&fire_dwlPointerListener, w); }
    if ((caps & 2) && w->keyboard == NULL) { w->keyboard = fire_dwl_new(l, seat, 1, l->iKeyboard); l->addListener(w->keyboard, (FireWlVoid*)(void*)&fire_dwlKeyboardListener, w); }
    if ((caps & 4) && w->touchDev == NULL) { w->touchDev = fire_dwl_new(l, seat, 2, l->iTouch); l->addListener(w->touchDev, (FireWlVoid*)(void*)&fire_dwlTouchListener, w); }
}

/* ---- the input method (text-input-v3) ---- */
static void fire_dwl_textCommitState(FireDwlWindow* w) {
    FireWlc* l = w->l;
    if (w->textInput == NULL) return;
    FIRE_WL_REQ(l, w->textInput, 6, w->textX, w->textY + fire_dwl_titleH(w), w->textW > 0 ? w->textW : 1, w->textH > 0 ? w->textH : 16);
    FIRE_WL_REQ0(l, w->textInput, 7);
}
static void fire_dwl_textEnter(void* data, void* t, void* surface) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)t;
    if (surface != w->surface) return;
    w->imActive = 1;
    FIRE_WL_REQ0(w->l, w->textInput, 1);   /* enable */
    FIRE_WL_REQ(w->l, w->textInput, 5, 0u, 0u);   /* set_content_type: none, normal */
    w->imEnabled = 1;
    fire_dwl_textCommitState(w);
}
static void fire_dwl_textLeave(void* data, void* t, void* surface) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)t; (void)surface;
    w->imActive = 0;
    if (w->imEnabled) { FIRE_WL_REQ0(w->l, w->textInput, 2); FIRE_WL_REQ0(w->l, w->textInput, 7); w->imEnabled = 0; }
    if (w->preedit != NULL) { free(w->preedit); w->preedit = NULL; fire_display_queueEditing(&w->queue, NULL, 0, 0, 0); }
}
static void fire_dwl_textPreedit(void* data, void* t, const char* text, int32_t begin, int32_t end) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int k, chars = 0;
    (void)t; (void)end;
    free(w->preedit);
    w->preedit = text != NULL && text[0] ? (char*)fire_display_copyBytes(text, strlen(text)) : NULL;
    /* (the cursor is a byte offset: in characters for the program) */
    for (k = 0; text != NULL && k < begin && text[k]; k++) if (((unsigned char)text[k] & 0xC0) != 0x80) chars++;
    w->preeditCursor = begin < 0 ? -1 : chars;
    w->imChanged = 1;
    w->imUsed = 1;
}
static void fire_dwl_textCommit(void* data, void* t, const char* text) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)t;
    free(w->commit);
    w->commit = text != NULL ? (char*)fire_display_copyBytes(text, strlen(text)) : NULL;
    w->imChanged = 1;
    w->imUsed = 1;
}
static void fire_dwl_textDelete(void* data, void* t, uint32_t before, uint32_t after) { (void)data; (void)t; (void)before; (void)after; }
static void fire_dwl_textDone(void* data, void* t, uint32_t serial) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)t; (void)serial;
    if (!w->imChanged) return;
    w->imChanged = 0;
    if (w->commit != NULL) { fire_display_queueText(&w->queue, w->commit, strlen(w->commit)); free(w->commit); w->commit = NULL; }
    if (w->preedit != NULL) fire_display_queueEditing(&w->queue, w->preedit, strlen(w->preedit), w->preeditCursor < 0 ? 0 : w->preeditCursor, 0);
    else fire_display_queueEditing(&w->queue, NULL, 0, 0, 0);
}
static const struct {
    void (*enter)(void*, void*, void*); void (*leave)(void*, void*, void*); void (*preedit)(void*, void*, const char*, int32_t, int32_t); void (*commit)(void*, void*, const char*);
    void (*del)(void*, void*, uint32_t, uint32_t); void (*done)(void*, void*, uint32_t);
} fire_dwlTextListener = { fire_dwl_textEnter, fire_dwl_textLeave, fire_dwl_textPreedit, fire_dwl_textCommit, fire_dwl_textDelete, fire_dwl_textDone };

/* ---- what other programs drag over the window, and the clipboard's offers ---- */
static FireDwlOffer* fire_dwl_offerOf(FireDwlWindow* w, void* offer) {
    int k;
    for (k = 0; k < 4; k++) if (w->offers[k].offer == offer) return &w->offers[k];
    return NULL;
}
static void fire_dwl_offerMime(void* data, void* offer, const char* mime) {
    FireDwlOffer* o = fire_dwl_offerOf((FireDwlWindow*)data, offer);
    if (o != NULL && o->mimeCount < 8 && strlen(mime) < sizeof o->mimes[0]) strcpy(o->mimes[o->mimeCount++], mime);
}
static void fire_dwl_offerActions(void* data, void* offer, uint32_t actions) { (void)data; (void)offer; (void)actions; }
static void fire_dwl_offerAction(void* data, void* offer, uint32_t action) { (void)data; (void)offer; (void)action; }
static const struct { void (*offer)(void*, void*, const char*); void (*sourceActions)(void*, void*, uint32_t); void (*action)(void*, void*, uint32_t); }
    fire_dwlOfferListener = { fire_dwl_offerMime, fire_dwl_offerActions, fire_dwl_offerAction };
static void fire_dwl_offerFree(FireDwlWindow* w, void* offer) {
    FireDwlOffer* o = fire_dwl_offerOf(w, offer);
    if (offer == NULL) return;
    FIRE_WL_REQ0(w->l, offer, 2);   /* destroy */
    w->l->destroy(offer);
    if (o != NULL) memset(o, 0, sizeof *o);
}
static int fire_dwl_offerHas(FireDwlWindow* w, void* offer, const char* mime) {
    FireDwlOffer* o = fire_dwl_offerOf(w, offer);
    int k;
    if (o == NULL) return 0;
    for (k = 0; k < o->mimeCount; k++) if (strcmp(o->mimes[k], mime) == 0) return 1;
    return 0;
}
static void fire_dwl_dataOffer(void* data, void* device, void* offer) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int k;
    (void)device;
    for (k = 0; k < 4; k++) if (w->offers[k].offer == NULL) break;
    if (k == 4) {
        /* (more offers alive than expected: the oldest that is neither the drag nor the selection goes) */
        for (k = 0; k < 4; k++) if (w->offers[k].offer != w->dragOffer && w->offers[k].offer != w->selectionOffer) { fire_dwl_offerFree(w, w->offers[k].offer); break; }
        if (k == 4) k = 0;
    }
    memset(&w->offers[k], 0, sizeof w->offers[k]);
    w->offers[k].offer = offer;
    w->l->addListener(offer, (FireWlVoid*)(void*)&fire_dwlOfferListener, w);
}
static const char* fire_dwl_dragPick(FireDwlWindow* w, void* offer) {
    static const char* const prefer[] = { "text/uri-list", "text/plain;charset=utf-8", "UTF8_STRING", "text/plain", NULL };
    int k;
    for (k = 0; prefer[k]; k++) if (fire_dwl_offerHas(w, offer, prefer[k])) return prefer[k];
    return NULL;
}
static void fire_dwl_dragEnter(void* data, void* device, uint32_t serial, void* surface, int32_t x, int32_t y, void* offer) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)device;
    if (surface != w->surface) return;
    if (w->dragOffer != NULL && w->dragOffer != offer) fire_dwl_offerFree(w, w->dragOffer);
    w->dragOffer = offer;
    w->dragSerial = serial;
    w->dragMime = offer != NULL ? fire_dwl_dragPick(w, offer) : NULL;
    w->dragX = (float)x / 256.0f; w->dragY = (float)y / 256.0f - (float)fire_dwl_titleH(w);
    if (offer != NULL) {
        FIRE_WL_REQ(w->l, offer, 0, serial, w->dragMime);   /* accept */
        if (w->ddmVersion >= 3) FIRE_WL_REQ(w->l, offer, 4, w->dragMime ? 1u : 0u, 1u);   /* set_actions: copy */
    }
    if (w->dragMime != NULL) fire_display_dropHover(&w->queue, w->dragX, w->dragY);
}
static void fire_dwl_dragLeave(void* data, void* device) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)device;
    if (w->dragOffer != NULL) { fire_dwl_offerFree(w, w->dragOffer); w->dragOffer = NULL; }
    fire_display_dropLeave(&w->queue);
}
static void fire_dwl_dragMotion(void* data, void* device, uint32_t time, int32_t x, int32_t y) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)device; (void)time;
    w->dragX = (float)x / 256.0f; w->dragY = (float)y / 256.0f - (float)fire_dwl_titleH(w);
    if (w->dragMime != NULL) fire_display_dropHover(&w->queue, w->dragX, w->dragY);
}
/* Reads what an offer gives as `mime` (malloc'd with a terminating 0; NULL: nothing). The events of the connection are taken in meanwhile (the other side may be this program). */
static unsigned char* fire_dwl_receive(FireDwlWindow* w, void* offer, const char* mime, size_t* n) {
    int fds[2];
    unsigned char* data = NULL;
    size_t size = 0, cap = 0;
    double until = fire_dwl_now() + 1.0;
    *n = 0;
    if (pipe(fds) != 0) return NULL;
    FIRE_WL_REQ(w->l, offer, 1, mime, fds[1]);   /* receive */
    close(fds[1]);
    w->l->flush(w->d);
    fcntl(fds[0], F_SETFL, O_NONBLOCK);
    for (;;) {
        struct pollfd p[2];
        p[0].fd = fds[0]; p[0].events = POLLIN; p[0].revents = 0;
        p[1].fd = w->l->getFd(w->d); p[1].events = POLLIN; p[1].revents = 0;
        if (fire_dwl_now() > until) break;
        poll(p, 2, 20);
        if (p[1].revents & POLLIN) {
            if (w->l->prepareRead(w->d) == 0) w->l->readEvents(w->d);
            w->l->dispatchPending(w->d);
            w->l->flush(w->d);
        }
        if (p[0].revents & (POLLIN | POLLHUP)) {
            ssize_t got;
            if (cap - size < 4096) { unsigned char* g = (unsigned char*)realloc(data, cap * 2 + 8192); if (g == NULL) break; data = g; cap = cap * 2 + 8192; }
            got = read(fds[0], data + size, cap - size - 1);
            if (got > 0) size += (size_t)got;
            else if (got == 0 || (errno != EAGAIN && errno != EINTR)) break;
        }
    }
    close(fds[0]);
    if (data != NULL) { data[size] = 0; *n = size; }
    return data;
}
static void fire_dwl_dragDrop(void* data, void* device) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)device;
    if (w->dragOffer != NULL && w->dragMime != NULL) {
        size_t n = 0;
        unsigned char* got = fire_dwl_receive(w, w->dragOffer, w->dragMime, &n);
        if (got != NULL) {
            if (strcmp(w->dragMime, "text/uri-list") == 0) {
                char* paths = fire_display_pathsFromUriList((const char*)got, n);
                if (paths != NULL) { fire_display_dropFile(&w->queue, paths, strlen(paths)); free(paths); }
            } else fire_display_dropText(&w->queue, (const char*)got, n);
            free(got);
        }
        if (w->ddmVersion >= 3) FIRE_WL_REQ0(w->l, w->dragOffer, 3);   /* finish */
        fire_display_dropDone(&w->queue, w->dragX, w->dragY);
    } else fire_display_dropLeave(&w->queue);
    if (w->dragOffer != NULL) { fire_dwl_offerFree(w, w->dragOffer); w->dragOffer = NULL; }
}
static void fire_dwl_selection(void* data, void* device, void* offer) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    (void)device;
    if (w->selectionOffer != NULL && w->selectionOffer != offer) fire_dwl_offerFree(w, w->selectionOffer);
    w->selectionOffer = offer;
}
static const struct {
    void (*dataOffer)(void*, void*, void*); void (*enter)(void*, void*, uint32_t, void*, int32_t, int32_t, void*); void (*leave)(void*, void*);
    void (*motion)(void*, void*, uint32_t, int32_t, int32_t); void (*drop)(void*, void*); void (*selection)(void*, void*, void*);
} fire_dwlDeviceListener = { fire_dwl_dataOffer, fire_dwl_dragEnter, fire_dwl_dragLeave, fire_dwl_dragMotion, fire_dwl_dragDrop, fire_dwl_selection };

/* ---- the clipboard (the window that has the keyboard offers what the program puts there; reading asks the compositor's selection) ---- */
typedef struct FireDwlClipOffer { char* text; unsigned char* png; size_t pngSize; unsigned char* bmp; size_t bmpSize; char* uris; char* gnome; } FireDwlClipOffer;
static FireDwlClipOffer g_fireDwlClip;
static void fire_dwl_clipForget(void) {
    free(g_fireDwlClip.text); free(g_fireDwlClip.png); free(g_fireDwlClip.bmp); free(g_fireDwlClip.uris); free(g_fireDwlClip.gnome);
    memset(&g_fireDwlClip, 0, sizeof g_fireDwlClip);
}
static void fire_dwl_sourceTarget(void* data, void* s, const char* mime) { (void)data; (void)s; (void)mime; }
static void fire_dwl_sourceSend(void* data, void* s, const char* mime, int32_t fd) {
    const unsigned char* bytes = NULL;
    size_t n = 0;
    (void)data; (void)s;
    if (g_fireDwlClip.text && (strcmp(mime, "text/plain;charset=utf-8") == 0 || strcmp(mime, "UTF8_STRING") == 0 || strcmp(mime, "text/plain") == 0 || strcmp(mime, "STRING") == 0 || strcmp(mime, "TEXT") == 0)) { bytes = (const unsigned char*)g_fireDwlClip.text; n = strlen(g_fireDwlClip.text); }
    else if (g_fireDwlClip.png && strcmp(mime, "image/png") == 0) { bytes = g_fireDwlClip.png; n = g_fireDwlClip.pngSize; }
    else if (g_fireDwlClip.bmp && strcmp(mime, "image/bmp") == 0) { bytes = g_fireDwlClip.bmp; n = g_fireDwlClip.bmpSize; }
    else if (g_fireDwlClip.uris && strcmp(mime, "text/uri-list") == 0) { bytes = (const unsigned char*)g_fireDwlClip.uris; n = strlen(g_fireDwlClip.uris); }
    else if (g_fireDwlClip.gnome && strcmp(mime, "x-special/gnome-copied-files") == 0) { bytes = (const unsigned char*)g_fireDwlClip.gnome; n = strlen(g_fireDwlClip.gnome); }
    while (n > 0) { ssize_t put = write(fd, bytes, n); if (put <= 0) { if (put < 0 && errno == EINTR) continue; break; } bytes += put; n -= (size_t)put; }
    close(fd);
}
static void fire_dwl_sourceCancelled(void* data, void* s) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    if (w->selectionSource == s) { w->selectionSource = NULL; fire_dwl_clipForget(); }
    w->l->destroy(s);
}
static void fire_dwl_sourceDummy(void* data, void* s) { (void)data; (void)s; }
static void fire_dwl_sourceAction(void* data, void* s, uint32_t a) { (void)data; (void)s; (void)a; }
static const struct {
    void (*target)(void*, void*, const char*); void (*send)(void*, void*, const char*, int32_t); void (*cancelled)(void*, void*);
    void (*dropPerformed)(void*, void*); void (*finished)(void*, void*); void (*action)(void*, void*, uint32_t);
} fire_dwlSourceListener = { fire_dwl_sourceTarget, fire_dwl_sourceSend, fire_dwl_sourceCancelled, fire_dwl_sourceDummy, fire_dwl_sourceDummy, fire_dwl_sourceAction };

/* The window the clipboard talks through, locked (NULL: no window has the keyboard - the program's own clipboard is used). */
static FireDwlWindow* fire_dwl_clipWindow(void) {
    FireDwlWindow* w;
    pthread_mutex_lock(&fire_dwlWindowsLock);
    w = fire_dwlFocused;
    if (w == NULL && fire_dwlWindowCount > 0) w = fire_dwlWindows[0];
    if (w != NULL) pthread_mutex_lock(&w->lock);
    pthread_mutex_unlock(&fire_dwlWindowsLock);
    if (w != NULL && (w->dataDevice == NULL || w->d == NULL)) { pthread_mutex_unlock(&w->lock); w = NULL; }
    return w;
}
/* Offers what is in g_fireDwlClip (filled by the caller) as the selection. */
static int fire_dwl_clipOffer(FireDwlWindow* w) {
    FireWlc* l = w->l;
    void* source;
    if (w->keySerial == 0) return 0;
    source = fire_dwl_new(l, w->ddm, 0, l->iSource);
    if (source == NULL) return 0;
    l->addListener(source, (FireWlVoid*)(void*)&fire_dwlSourceListener, w);
    if (g_fireDwlClip.text) { FIRE_WL_REQ(l, source, 0, "text/plain;charset=utf-8"); FIRE_WL_REQ(l, source, 0, "UTF8_STRING"); FIRE_WL_REQ(l, source, 0, "text/plain"); FIRE_WL_REQ(l, source, 0, "STRING"); FIRE_WL_REQ(l, source, 0, "TEXT"); }
    if (g_fireDwlClip.png) FIRE_WL_REQ(l, source, 0, "image/png");
    if (g_fireDwlClip.bmp) FIRE_WL_REQ(l, source, 0, "image/bmp");
    if (g_fireDwlClip.uris) { FIRE_WL_REQ(l, source, 0, "text/uri-list"); FIRE_WL_REQ(l, source, 0, "x-special/gnome-copied-files"); }
    if (w->selectionSource != NULL) { FIRE_WL_REQ0(l, w->selectionSource, 1); l->destroy(w->selectionSource); }
    w->selectionSource = source;
    FIRE_WL_REQ(l, w->dataDevice, 1, source, w->keySerial);   /* set_selection */
    l->flush(w->d);
    return 1;
}
static unsigned char* fire_dwl_clipRead(FireDwlWindow* w, const char* mime, size_t* n) {
    *n = 0;
    w->l->roundtrip(w->d);   /* (the latest selection) */
    if (w->selectionOffer == NULL || !fire_dwl_offerHas(w, w->selectionOffer, mime)) return NULL;
    return fire_dwl_receive(w, w->selectionOffer, mime, n);
}

static int fire_dwl_clipboardSet(const char* text) {
    FireDwlWindow* w = fire_dwl_clipWindow();
    int ok;
    if (w == NULL) return fire_display_memSetText(text);
    fire_dwl_clipForget();
    g_fireDwlClip.text = (char*)fire_display_copyBytes(text, strlen(text));
    ok = g_fireDwlClip.text != NULL && fire_dwl_clipOffer(w);
    pthread_mutex_unlock(&w->lock);
    return ok;
}
static char* fire_dwl_clipboardGet(void) {
    FireDwlWindow* w = fire_dwl_clipWindow();
    char* text = NULL;
    size_t n = 0;
    if (w == NULL) return fire_display_memGetText();
    if (w->selectionSource != NULL && g_fireDwlClip.text) text = (char*)fire_display_copyBytes(g_fireDwlClip.text, strlen(g_fireDwlClip.text));
    else {
        text = (char*)fire_dwl_clipRead(w, "text/plain;charset=utf-8", &n);
        if (text == NULL) text = (char*)fire_dwl_clipRead(w, "UTF8_STRING", &n);
        if (text == NULL) text = (char*)fire_dwl_clipRead(w, "text/plain", &n);
    }
    pthread_mutex_unlock(&w->lock);
    if (text != NULL && text[0] == 0) { free(text); text = NULL; }
    return text;
}
static int fire_dwl_clipboardHas(void) { char* t = fire_dwl_clipboardGet(); int has = t != NULL; free(t); return has; }
static unsigned char* fire_dwl_clipboardGetImage(size_t* size) {
    FireDwlWindow* w = fire_dwl_clipWindow();
    unsigned char* image = NULL;
    *size = 0;
    if (w == NULL) return fire_display_memGetImage(size);
    if (w->selectionSource != NULL) { if (g_fireDwlClip.png) { image = fire_display_copyBytes(g_fireDwlClip.png, g_fireDwlClip.pngSize); if (image) *size = g_fireDwlClip.pngSize; } }
    else {
        image = fire_dwl_clipRead(w, "image/png", size);
        if (image == NULL) image = fire_dwl_clipRead(w, "image/bmp", size);
    }
    pthread_mutex_unlock(&w->lock);
    return image;
}
static int fire_dwl_clipboardHasImage(void) { size_t n; unsigned char* i = fire_dwl_clipboardGetImage(&n); int has = i != NULL; free(i); return has; }
static int fire_dwl_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) {
    FireDwlWindow* w = fire_dwl_clipWindow();
    int ok;
    if (w == NULL) return fire_display_memSetImage(png, pngSize);
    fire_dwl_clipForget();
    g_fireDwlClip.png = fire_display_copyBytes(png, pngSize); g_fireDwlClip.pngSize = pngSize;
    if (bmp != NULL && bmpSize > 0) { g_fireDwlClip.bmp = fire_display_copyBytes(bmp, bmpSize); g_fireDwlClip.bmpSize = bmpSize; }
    ok = g_fireDwlClip.png != NULL && fire_dwl_clipOffer(w);
    pthread_mutex_unlock(&w->lock);
    return ok;
}
static char* fire_dwl_clipboardGetFiles(void) {
    FireDwlWindow* w = fire_dwl_clipWindow();
    char* paths = NULL;
    size_t n = 0;
    unsigned char* list;
    if (w == NULL) return fire_display_memGetFiles();
    if (w->selectionSource != NULL) list = g_fireDwlClip.uris ? fire_display_copyBytes(g_fireDwlClip.uris, n = strlen(g_fireDwlClip.uris)) : NULL;
    else {
        list = fire_dwl_clipRead(w, "text/uri-list", &n);
        if (list == NULL) list = fire_dwl_clipRead(w, "x-special/gnome-copied-files", &n);
    }
    pthread_mutex_unlock(&w->lock);
    if (list != NULL) { paths = fire_display_pathsFromUriList((const char*)list, n); free(list); }
    return paths;
}
static int fire_dwl_clipboardHasFiles(void) { char* p = fire_dwl_clipboardGetFiles(); int has = p != NULL; free(p); return has; }
static int fire_dwl_clipboardSetFiles(const char* paths) {
    FireDwlWindow* w = fire_dwl_clipWindow();
    int ok = 0;
    if (w == NULL) return fire_display_memSetFiles(paths);
    fire_dwl_clipForget();
    g_fireDwlClip.uris = fire_display_uriListFromPaths(paths);
    if (g_fireDwlClip.uris != NULL) {
        size_t n = strlen(g_fireDwlClip.uris);
        g_fireDwlClip.gnome = (char*)malloc(n + 6);
        if (g_fireDwlClip.gnome != NULL) { memcpy(g_fireDwlClip.gnome, "copy\n", 5); memcpy(g_fireDwlClip.gnome + 5, g_fireDwlClip.uris, n + 1); }
        ok = g_fireDwlClip.gnome != NULL && fire_dwl_clipOffer(w);
    }
    pthread_mutex_unlock(&w->lock);
    return ok;
}

/* ---- buffers ---- */
static int fire_dwl_memfd(size_t size) {
    int fd = -1;
#if defined(__linux__) && defined(SYS_memfd_create)
    {
        extern long syscall(long, ...);   /* (declared by <unistd.h> only with _GNU_SOURCE/_DEFAULT_SOURCE) */
        fd = (int)syscall(SYS_memfd_create, "fire-display", 1u /* MFD_CLOEXEC */);
    }
#endif
    if (fd < 0) {
        char name[64];
        snprintf(name, sizeof name, "/fire-display-%ld-%ld", (long)getpid(), (long)(fire_dwl_now() * 1e6));
        fd = shm_open(name, O_RDWR | O_CREAT | O_EXCL, 0600);
        if (fd >= 0) shm_unlink(name);
    }
    if (fd >= 0 && ftruncate(fd, (off_t)size) != 0) { close(fd); fd = -1; }
    return fd;
}
static void fire_dwl_bufferRelease(void* data, void* buffer) {
    FireDwlWindow* w = (FireDwlWindow*)data;
    int k;
    for (k = 0; k < 2; k++) if (w->buffers[k].buffer == buffer) w->buffers[k].busy = 0;
}
static const struct { void (*release)(void*, void*); } fire_dwlBufferListener = { fire_dwl_bufferRelease };
static void fire_dwl_freeBuffer(FireDwlWindow* w, FireDwlBuffer* b) {
    if (b->buffer) { FIRE_WL_REQ0(w->l, b->buffer, 0); w->l->destroy(b->buffer); }
    if (b->data) munmap(b->data, b->size);
    memset(b, 0, sizeof *b);
}
static FireDwlBuffer* fire_dwl_getBuffer(FireDwlWindow* w, int width, int height) {
    FireWlc* l = w->l;
    int k;
    for (k = 0; k < 2; k++) if (w->buffers[k].buffer && (w->buffers[k].width != width || w->buffers[k].height != height) && !w->buffers[k].busy) fire_dwl_freeBuffer(w, &w->buffers[k]);
    for (k = 0; k < 2; k++) if (w->buffers[k].buffer && !w->buffers[k].busy) return &w->buffers[k];
    for (k = 0; k < 2; k++) if (w->buffers[k].buffer == NULL) {
        FireDwlBuffer* b = &w->buffers[k];
        size_t size = (size_t)width * (size_t)height * 4;
        int fd = fire_dwl_memfd(size);
        void* pool;
        if (fd < 0) return NULL;
        b->data = (uint32_t*)mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        if (b->data == MAP_FAILED) { b->data = NULL; close(fd); return NULL; }
        pool = l->marshalConstructor(w->shm, 0, l->iShmPool, NULL, fd, (int32_t)size);   /* wl_shm.create_pool */
        b->buffer = l->marshalConstructor(pool, 0, l->iBuffer, NULL, 0, width, height, width * 4, 0u /* ARGB8888 */);
        FIRE_WL_REQ0(l, pool, 1);   /* destroy the pool (the buffer keeps the memory) */
        l->destroy(pool);
        close(fd);
        b->size = size; b->width = width; b->height = height; b->busy = 0; b->dirtyAll = 1;
        l->addListener(b->buffer, (FireWlVoid*)(void*)&fire_dwlBufferListener, w);
        return b;
    }
    return NULL;
}

static void fire_dwl_frameDone(void* data, void* cb, uint32_t time) { FireDwlWindow* w = (FireDwlWindow*)data; (void)time; w->l->destroy(cb); w->frameCallback = NULL; w->framePending = 0; }
static const struct { void (*done)(void*, void*, uint32_t); } fire_dwlFrameListener = { fire_dwl_frameDone };

/* Takes in what the compositor sent (waits at most `ms` for it). */
static void fire_dwl_dispatch(FireDwlWindow* w, int ms) {
    FireWlc* l = w->l;
    struct pollfd p;
    l->dispatchPending(w->d);
    l->flush(w->d);
    if (l->prepareRead(w->d) != 0) { l->dispatchPending(w->d); return; }
    p.fd = l->getFd(w->d); p.events = POLLIN; p.revents = 0;
    if (poll(&p, 1, ms) > 0 && (p.revents & POLLIN)) l->readEvents(w->d); else l->cancelRead(w->d);
    l->dispatchPending(w->d);
    if (l->displayError(w->d) != 0) w->closed = 1;
}

/* ---- open, close ---- */
static int fire_dwl_open(FireDwlWindow* w, const char* title, int width, int height, int vsync) {
    FireWlc* l = fire_dwl_lib();
    int k;
    if (l == NULL || width <= 0 || height <= 0) return 0;
    w->l = l;
    w->d = l->connect(NULL);
    if (w->d == NULL) return 0;
    w->vsync = vsync;
    w->winW = width; w->winH = height;
    w->title = (char*)fire_display_copyBytes(title ? title : "", strlen(title ? title : ""));
    fire_display_queueInit(&w->queue);
    { FireSystemTheme t; fire_display_systemTheme(&t); w->dark = t.dark; }
    w->registry = fire_dwl_new(l, w->d, 1, l->iRegistry);   /* wl_display.get_registry */
    l->addListener(w->registry, (FireWlVoid*)(void*)&fire_dwlRegistryListener, w);
    l->roundtrip(w->d);
    l->roundtrip(w->d);   /* (the seat's capabilities and the outputs' scales) */
    if (w->compositor == NULL || w->shm == NULL || w->wmBase == NULL) { l->disconnect(w->d); w->d = NULL; free(w->title); w->title = NULL; return 0; }
    w->surface = fire_dwl_new(l, w->compositor, 0, l->iSurface);
    l->addListener(w->surface, (FireWlVoid*)(void*)&fire_dwlSurfaceListener, w);
    if (w->fracMgr != NULL && w->viewporter != NULL) {
        w->frac = fire_dwl_new1(l, w->fracMgr, 1, &fire_wlFrac, w->surface);
        l->addListener(w->frac, (FireWlVoid*)(void*)&fire_dwlFracListener, w);
        w->viewport = fire_dwl_new1(l, w->viewporter, 1, &fire_wlViewport, w->surface);
    }
    w->xdgSurface = fire_dwl_new1(l, w->wmBase, 2, &fire_wlXdgSurface, w->surface);
    l->addListener(w->xdgSurface, (FireWlVoid*)(void*)&fire_dwlXdgSurfaceListener, w);
    w->toplevel = fire_dwl_new(l, w->xdgSurface, 1, &fire_wlXdgToplevel);
    l->addListener(w->toplevel, (FireWlVoid*)(void*)&fire_dwlToplevelListener, w);
    FIRE_WL_REQ(l, w->toplevel, 2, w->title);
    FIRE_WL_REQ(l, w->toplevel, 3, "fire");
    w->csd = fire_dwl_ownTitleBar();
    if (w->decoMgr != NULL) {
        w->deco = fire_dwl_new1(l, w->decoMgr, 1, &fire_wlDeco, w->toplevel);
        l->addListener(w->deco, (FireWlVoid*)(void*)&fire_dwlDecoListener, w);
        FIRE_WL_REQ(l, w->deco, 1, 2u);   /* server side, please */
    }
    if (w->ddm != NULL && w->seat != NULL) {
        w->dataDevice = l->marshalConstructor(w->ddm, 1, l->iDevice, NULL, w->seat);
        l->addListener(w->dataDevice, (FireWlVoid*)(void*)&fire_dwlDeviceListener, w);
    }
    if (w->textMgr != NULL && w->seat != NULL) {
        w->textInput = l->marshalConstructor(w->textMgr, 1, &fire_wlText, NULL, w->seat);
        l->addListener(w->textInput, (FireWlVoid*)(void*)&fire_dwlTextListener, w);
    }
    FIRE_WL_REQ0(l, w->surface, 6);   /* commit: the first configure comes */
    for (k = 0; k < 100 && !w->configured && !w->closed; k++) fire_dwl_dispatch(w, 20);
    if (!w->configured) { w->closed = 1; }
    pthread_mutex_lock(&fire_dwlWindowsLock);
    if (fire_dwlWindowCount < 64) fire_dwlWindows[fire_dwlWindowCount++] = w;
    pthread_mutex_unlock(&fire_dwlWindowsLock);
    if (w->closed) return 0;
#ifdef FIRE_DISPLAY_HAS_SELFTEST
    fire_display_selftest(&w->queue, width, height);
#endif
    return 1;
}

static void fire_dwl_close(FireDwlWindow* w) {
    FireWlc* l = w->l;
    int k;
    if (l == NULL || w->d == NULL) return;
    pthread_mutex_lock(&fire_dwlWindowsLock);
    for (k = 0; k < fire_dwlWindowCount; k++) if (fire_dwlWindows[k] == w) { fire_dwlWindows[k] = fire_dwlWindows[--fire_dwlWindowCount]; break; }
    if (fire_dwlFocused == w) fire_dwlFocused = NULL;
    pthread_mutex_unlock(&fire_dwlWindowsLock);
    pthread_mutex_lock(&w->lock);
    if (fire_wl.display == w->d) fire_dragout_wlForget();   /* (the drag-out of fire_dragout.h listens on one connection: this one) */
    for (k = 0; k < 2; k++) fire_dwl_freeBuffer(w, &w->buffers[k]);
    for (k = 0; k < 4; k++) if (w->offers[k].offer) fire_dwl_offerFree(w, w->offers[k].offer);
    if (w->selectionSource) { FIRE_WL_REQ0(l, w->selectionSource, 1); l->destroy(w->selectionSource); w->selectionSource = NULL; }
    if (w->frameCallback) l->destroy(w->frameCallback);
    fire_dwl_destroyRequest(l, w->textInput, 0);
    if (w->dataDevice) { if (w->ddmVersion >= 2) FIRE_WL_REQ0(l, w->dataDevice, 2); l->destroy(w->dataDevice); }
    fire_dwl_destroyRequest(l, w->shapeDevice, 0);
    fire_dwl_destroyRequest(l, w->kdeBlur, 2);
    fire_dwl_destroyRequest(l, w->bgEffect, 0);
    fire_dwl_destroyRequest(l, w->deco, 0);
    fire_dwl_destroyRequest(l, w->toplevel, 0);
    fire_dwl_destroyRequest(l, w->xdgSurface, 0);
    fire_dwl_destroyRequest(l, w->viewport, 0);
    fire_dwl_destroyRequest(l, w->frac, 0);
    fire_dwl_destroyRequest(l, w->surface, 0);
    fire_dwl_destroyRequest(l, w->cursorSurface, 0);
    if (w->cursorTheme && l->cursorThemeDestroy) l->cursorThemeDestroy(w->cursorTheme);
    /* the objects of the connection (the protocol's destructors where there are some; the proxies in any case) */
    if (w->pointer) { if (w->seatVersion >= 3) FIRE_WL_REQ0(l, w->pointer, 1); l->destroy(w->pointer); }
    if (w->keyboard) { if (w->seatVersion >= 3) FIRE_WL_REQ0(l, w->keyboard, 0); l->destroy(w->keyboard); }
    if (w->touchDev) { if (w->seatVersion >= 3) FIRE_WL_REQ0(l, w->touchDev, 0); l->destroy(w->touchDev); }
    if (w->seat) { if (w->seatVersion >= 5) FIRE_WL_REQ0(l, w->seat, 0); l->destroy(w->seat); }
    fire_dwl_destroyRequest(l, w->wmBase, 0);
    fire_dwl_destroyRequest(l, w->decoMgr, 0);
    fire_dwl_destroyRequest(l, w->viewporter, 0);
    fire_dwl_destroyRequest(l, w->fracMgr, 0);
    fire_dwl_destroyRequest(l, w->textMgr, 0);
    fire_dwl_destroyRequest(l, w->shapeMgr, 0);
    fire_dwl_destroyRequest(l, w->bgMgr, 0);
    if (w->kdeBlurMgr) l->destroy(w->kdeBlurMgr);
    if (w->ddm) l->destroy(w->ddm);
    for (k = 0; k < w->outputCount; k++) l->destroy(w->outputs[k]);
    if (w->compositor) l->destroy(w->compositor);
    if (w->shm) l->destroy(w->shm);
    if (w->registry) l->destroy(w->registry);
    if (w->composeState) l->xkbComposeStateUnref(w->composeState);
    if (w->composeTable) l->xkbComposeTableUnref(w->composeTable);
    if (w->xkbState) l->xkbStateUnref(w->xkbState);
    if (w->keymap) l->xkbKeymapUnref(w->keymap);
    if (w->xkbContext) l->xkbContextUnref(w->xkbContext);
    l->flush(w->d);
    l->disconnect(w->d);
    w->d = NULL;
    free(w->title); w->title = NULL;
    free(w->lastFrame); w->lastFrame = NULL;
    free(w->preedit); w->preedit = NULL;
    free(w->commit); w->commit = NULL;
    fire_display_queueFree(&w->queue);
    pthread_mutex_unlock(&w->lock);
}

static void fire_dwl_setVSync(FireDwlWindow* w, int v) { w->vsync = v; }
static void fire_dwl_setTouchMouse(FireDwlWindow* w, int v) { w->touchMouse = v; }
/* (Wayland: a client cannot size its window from outside - the next frame has the new size, as the compositor sees it) */
static void fire_dwl_setSize(FireDwlWindow* w, int width, int height) { w->winW = width; w->winH = height; }
static void fire_dwl_setCursor(FireDwlWindow* w, int c) {
    if (c == w->cursor) return;
    w->cursor = c;
    pthread_mutex_lock(&w->lock);
    if (w->pointerInside == 1) { fire_dwl_applyCursor(w, c); w->l->flush(w->d); }
    pthread_mutex_unlock(&w->lock);
}
static void fire_dwl_setTextInputRect(FireDwlWindow* w, int x, int y, int width, int height) {
    w->textX = x; w->textY = y; w->textW = width; w->textH = height;
    pthread_mutex_lock(&w->lock);
    if (w->imEnabled) { fire_dwl_textCommitState(w); w->l->flush(w->d); }
    pthread_mutex_unlock(&w->lock);
}
static int fire_dwl_setBackdrop(FireDwlWindow* w, int kind) {
    FireWlc* l = w->l;
    if (w->d == NULL) return FIRE_BACKDROP_NONE;
    pthread_mutex_lock(&w->lock);
    w->backdrop = kind <= FIRE_BACKDROP_NONE ? FIRE_BACKDROP_NONE : FIRE_BACKDROP_TRANSPARENT;
    if (kind >= FIRE_BACKDROP_BLUR) {
        /* the compositor blurs what lies behind the whole surface (a NULL region: all of it) */
        if (w->bgMgr != NULL) {
            if (w->bgEffect == NULL) w->bgEffect = fire_dwl_new1(l, w->bgMgr, 1, &fire_wlBg, w->surface);
            if (w->bgEffect != NULL) {
                void* region = fire_dwl_new(l, w->compositor, 1, l->iRegion);
                FIRE_WL_REQ(l, region, 1, 0, 0, w->winW, w->winH + fire_dwl_titleH(w));   /* add */
                FIRE_WL_REQ(l, w->bgEffect, 1, region);
                FIRE_WL_REQ0(l, region, 0); l->destroy(region);
                w->backdrop = FIRE_BACKDROP_BLUR;
            }
        } else if (w->kdeBlurMgr != NULL) {
            if (w->kdeBlur == NULL) w->kdeBlur = fire_dwl_new1(l, w->kdeBlurMgr, 0, &fire_wlKdeBlur, w->surface);
            if (w->kdeBlur != NULL) { FIRE_WL_REQ(l, w->kdeBlur, 1, (void*)NULL); FIRE_WL_REQ0(l, w->kdeBlur, 0); w->backdrop = FIRE_BACKDROP_BLUR; }
        }
    } else {
        if (w->bgEffect != NULL) { FIRE_WL_REQ(l, w->bgEffect, 1, (void*)NULL); }
        if (w->kdeBlur != NULL) { fire_dwl_destroyRequest(l, w->kdeBlur, 2); w->kdeBlur = NULL; if (w->kdeBlurMgr) FIRE_WL_REQ(l, w->kdeBlurMgr, 1, w->surface); }
    }
    l->flush(w->d);
    pthread_mutex_unlock(&w->lock);
    return w->backdrop;
}
static double fire_dwl_scale(FireDwlWindow* w) { return fire_dwl_scaleOf(w); }

static void fire_dwl_draw(FireDwlWindow* w);

static int fire_dwl_pump(FireDwlWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    int wasMax, wasAct;
    if (w->d == NULL) return 0;
    pthread_mutex_lock(&w->lock);
    wasMax = w->maximized; wasAct = w->activated;
    fire_dwl_dispatch(w, 0);
    /* the repeat of a held key */
    if (w->repeating && w->hasFocus && w->repeatRate > 0) {
        double now = fire_dwl_now();
        int n = 0;
        while (now >= w->repeatNext && n++ < 8) { fire_dwl_keyPress(w, w->repeatKey, 1, 1); w->repeatNext += 1.0 / w->repeatRate; }
        if (now >= w->repeatNext) w->repeatNext = now + 1.0 / w->repeatRate;
    }
    fire_dragout_wlPump(w->d);
    if (w->csd && (wasMax != w->maximized || wasAct != w->activated) && w->lastFrame != NULL && !w->direct) fire_dwl_draw(w);
    if (w->closed) fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE);
    fire_display_queueTake(&w->queue, out, w->winW, w->winH, fbWidth, fbHeight);
    pthread_mutex_unlock(&w->lock);
    return !w->queue.quit;
}

/* Draws the last framebuffer (and the title bar) into a free buffer and shows it. The caller holds the lock. */
static void fire_dwl_draw(FireDwlWindow* w) {
    FireWlc* l = w->l;
    double s = fire_dwl_scaleOf(w);
    int T = fire_dwl_titleH(w), lw = w->winW, lh = w->winH + T;
    int bw = (int)(lw * s + 0.5), bh = (int)(lh * s + 0.5), th = (int)(T * s + 0.5), ch = bh - th, row, r0, r1, c0, c1;
    const uint32_t* rgba = w->lastFrame;
    int width = w->lastW, height = w->lastH, opaque = w->backdrop == FIRE_BACKDROP_NONE;
    FireDwlBuffer* b;
    if (bw <= 0 || ch <= 0 || rgba == NULL) return;
    b = fire_dwl_getBuffer(w, bw, bh);
    if (b == NULL) return;
    if (th > 0) fire_dwl_drawTitle(w, b->data, bw, th, s);
    /* unstretched, the buffer only gets what changed since it was drawn last (it holds an older frame); stretched or new, all of it */
    {
        int unstretched = bw == width && ch == height;
        if (!unstretched || b->dirtyAll) { r0 = 0; r1 = ch; c0 = 0; c1 = bw; }
        else { c0 = b->dirty[0]; r0 = b->dirty[1]; c1 = b->dirty[2]; r1 = b->dirty[3]; }
        b->dirtyAll = 0; b->dirty[0] = b->dirty[1] = b->dirty[2] = b->dirty[3] = 0;
        if (!unstretched) w->damageAll = 1;
    }
    for (row = r0; row < r1; row++) {
        const uint32_t* src = rgba + (size_t)(ch == height ? row : (int)((long long)row * height / ch)) * (size_t)width;
        uint32_t* d = b->data + (size_t)(row + th) * (size_t)bw;
        uint32_t step = (uint32_t)(((uint64_t)width << 16) / (uint32_t)bw), pos = step / 2 + step * (uint32_t)c0;
        int col;
        for (col = c0; col < c1; col++) {
            uint32_t v = bw == width ? src[col] : src[(pos += step, (pos - step) >> 16)];
            uint32_t a = opaque ? 255u : v >> 24;
            if (a == 255u) d[col] = 0xFF000000u | ((v & 0xFFu) << 16) | (v & 0xFF00u) | ((v >> 16) & 0xFFu);
            else d[col] = (a << 24) | (((v & 0xFFu) * a / 255u) << 16) | ((((v >> 8) & 0xFFu) * a / 255u) << 8) | (((v >> 16) & 0xFFu) * a / 255u);
        }
    }
    if (w->viewport != NULL && w->fracScale120 > 0) FIRE_WL_REQ(l, w->viewport, 2, lw, lh);   /* set_destination: the logical size */
    else if (w->compositorVersion >= 3) FIRE_WL_REQ(l, w->surface, 8, (int32_t)(s + 0.5));   /* set_buffer_scale */
    if (w->csd) FIRE_WL_REQ(l, w->xdgSurface, 3, 0, 0, lw, lh);   /* set_window_geometry */
    FIRE_WL_REQ(l, w->surface, 1, b->buffer, 0, 0);   /* attach */
    if (w->compositorVersion >= 4 && !w->damageAll) {
        /* damage_buffer: only what changed, below the title bar (nothing: none - the commit still counts for VSync) */
        if (w->damage[2] > w->damage[0] && w->damage[3] > w->damage[1]) FIRE_WL_REQ(l, w->surface, 9, w->damage[0], w->damage[1] + th, w->damage[2] - w->damage[0], w->damage[3] - w->damage[1]);
    }
    else if (w->compositorVersion >= 4) FIRE_WL_REQ(l, w->surface, 9, 0, 0, bw, bh);   /* damage_buffer */
    else FIRE_WL_REQ(l, w->surface, 2, 0, 0, lw, lh);   /* damage */
    w->damageAll = 1; w->damage[0] = w->damage[1] = w->damage[2] = w->damage[3] = 0;   /* (a draw without a present - the title bar: all of it) */
    if (w->frameCallback == NULL) {
        w->frameCallback = fire_dwl_new(l, w->surface, 3, l->iCallback);   /* frame */
        l->addListener(w->frameCallback, (FireWlVoid*)(void*)&fire_dwlFrameListener, w);
        w->framePending = 1;
    }
    FIRE_WL_REQ0(l, w->surface, 6);   /* commit */
    b->busy = 1;
    l->flush(w->d);
}

/* (x0, y0, x1, y1) into the union `a` (an empty `a` - x1 <= x0 - takes it as it is) */
static void fire_dwl_union(int* a, int x0, int y0, int x1, int y1) {
    if (x1 <= x0 || y1 <= y0) return;
    if (a[2] <= a[0] || a[3] <= a[1]) { a[0] = x0; a[1] = y0; a[2] = x1; a[3] = y1; return; }
    if (x0 < a[0]) a[0] = x0;
    if (y0 < a[1]) a[1] = y0;
    if (x1 > a[2]) a[2] = x1;
    if (y1 > a[3]) a[3] = y1;
}

/* Shows the frame; only (ax, ay, aw, ah) changed since the last one: both buffers note it (each gets what it misses when it is drawn next), the compositor hears
 * only of it. A frame of another size is all of it. */
static void fire_dwl_presentArea(FireDwlWindow* w, const uint32_t* rgba, int width, int height, int ax, int ay, int aw, int ah) {
    size_t n;
    int k, all, x0, y0, x1, y1;
    if (w->d == NULL || w->direct || rgba == NULL || width <= 0 || height <= 0) return;
    pthread_mutex_lock(&w->lock);
    /* VSync: the compositor's frame callback of the frame before (at most 50 ms - a hidden window gets none) */
    if (w->vsync && w->framePending) {
        double until = fire_dwl_now() + 0.05;
        while (w->framePending && !w->closed && fire_dwl_now() < until) fire_dwl_dispatch(w, 5);
    }
    n = (size_t)width * (size_t)height;
    all = w->lastFrame == NULL || w->lastW != width || w->lastH != height;
    x0 = ax < 0 ? 0 : ax > width ? width : ax; x1 = ax + aw < 0 ? 0 : ax + aw > width ? width : ax + aw;
    y0 = ay < 0 ? 0 : ay > height ? height : ay; y1 = ay + ah < 0 ? 0 : ay + ah > height ? height : ay + ah;
    for (k = 0; k < 2; k++) { if (all) w->buffers[k].dirtyAll = 1; else fire_dwl_union(w->buffers[k].dirty, x0, y0, x1, y1); }
    w->damageAll = all;
    w->damage[0] = w->damage[1] = w->damage[2] = w->damage[3] = 0;
    if (!all) fire_dwl_union(w->damage, x0, y0, x1, y1);
    if (w->lastFrame == NULL || w->lastW != width || w->lastH != height) {
        uint32_t* grown = (uint32_t*)realloc(w->lastFrame, n * 4);
        if (grown == NULL) { pthread_mutex_unlock(&w->lock); return; }
        w->lastFrame = grown; w->lastW = width; w->lastH = height;
    }
    memcpy(w->lastFrame, rgba, n * 4);
    if (w->buffers[0].busy && w->buffers[1].busy) fire_dwl_dispatch(w, 2);
    fire_dwl_draw(w);
    pthread_mutex_unlock(&w->lock);
}

static void fire_dwl_present(FireDwlWindow* w, const uint32_t* rgba, int width, int height) { fire_dwl_presentArea(w, rgba, width, height, 0, 0, width, height); }

static int fire_dwl_native(FireDwlWindow* w, fire_native_window* out) {
    if (w->d == NULL || w->csd) return 0;   /* (fire's own title bar is in the buffer of the surface: the window shows the pixels) */
    out->kind = FIRE_NATIVE_WAYLAND;
    out->a = w->d;
    out->b = w->surface;
    out->width = (int)(w->winW * fire_dwl_scaleOf(w) + 0.5); out->height = (int)(w->winH * fire_dwl_scaleOf(w) + 0.5);
    w->direct = 1;
    return 1;
}
static void fire_dwl_restore(FireDwlWindow* w) { w->direct = 0; }

/* Dragging text and files out of the window: wl_data_device.start_drag (fire_dragout.h, on the window's connection with a queue of its own). */
static int fire_dwl_startDrag(FireDwlWindow* w, const char* text, const char* files) {
    int result;
    if (w->d == NULL) return 0;
    pthread_mutex_lock(&w->lock);
    result = fire_dragout_wayland(w->d, w->surface, text, files);
    pthread_mutex_unlock(&w->lock);
    return result;
}
static int fire_dwl_canStartDrag(FireDwlWindow* w) {
    int ready;
    if (w->d == NULL) return 0;
    pthread_mutex_lock(&w->lock);
    ready = fire_dragout_wlReady(w->d);
    pthread_mutex_unlock(&w->lock);
    return ready;
}

#endif
