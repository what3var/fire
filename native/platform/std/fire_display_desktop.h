/* fire native platform package "std": fire's own display of a desktop - no SDL. It picks, when the first window opens, which window system it talks to, and forwards
 * the functions of the HAL (fire_display_c.h) to that display:
 *   Linux and the BSDs   Wayland (fire_display_wayland.h) when WAYLAND_DISPLAY is set and libwayland-client loads, else X11 (fire_display_x11.h) when DISPLAY is set
 *   Windows              Win32 (fire_display_win32.h)
 *   macOS                Cocoa (fire_display_cocoa.h)
 *   the browser          the canvas of the page (fire_display_web.h)
 *   everywhere           no screen (fire_display_headless.h): FIRE_DISPLAY=headless - the tests, a server
 * FIRE_DISPLAY=x11|wayland|win32|cocoa|web|headless asks for one (SDL_VIDEODRIVER=dummy|offscreen|x11|wayland is understood as well, as the SDL displays did). Nothing
 * of a window system is linked that the operating system does not always have: Linux loads its libraries when it needs them (dlopen); Windows links user32/gdi32 and
 * loads the rest; macOS links its frameworks.
 * The numbers of keys, modifiers, buttons and cursors are fire_display_keys.h's; the look of the desktop comes from fire_systheme.h; joysticks and gamepads from
 * fire_display_joystick.h. */
#ifndef FIRE_DISPLAY_DESKTOP_H
#define FIRE_DISPLAY_DESKTOP_H

#include <stdlib.h>
#include <string.h>
#include "fire_display_common.h"
#include "fire_systheme.h"

#if defined(__EMSCRIPTEN__)
#  define FIRE_DD_WEB 1
#elif defined(_WIN32)
#  define FIRE_DD_WIN32 1
#elif defined(__APPLE__)
#  define FIRE_DD_COCOA 1
#elif !defined(FIRE_DISPLAY_NO_X11) || !defined(FIRE_DISPLAY_NO_WAYLAND)
#  ifndef FIRE_DISPLAY_NO_WAYLAND
#    define FIRE_DD_WAYLAND 1
#  endif
#  ifndef FIRE_DISPLAY_NO_X11
#    define FIRE_DD_X11 1
#  endif
#endif

#ifdef FIRE_DD_WAYLAND
#  define FIRE_DRAGOUT_WANT_WAYLAND 1   /* (fire_dragout.h with its Wayland part: the X11 display includes it first) */
#endif
#include "fire_display_headless.h"
#ifdef FIRE_DD_X11
#  include "fire_display_x11.h"
#endif
#ifdef FIRE_DD_WAYLAND
#  include "fire_display_wayland.h"
#endif
#ifdef FIRE_DD_WIN32
#  include "fire_display_win32.h"
#endif
#ifdef FIRE_DD_COCOA
#  include "fire_display_cocoa.h"
#endif
#ifdef FIRE_DD_WEB
#  include "fire_display_web.h"
#endif

#include "fire_display_joystick.h"

#define FIRE_DISPLAY_AVAILABLE 1
#define FIRE_DISPLAY_TEXT_INPUT_RECT 1
#define FIRE_DISPLAY_BACKDROP 1
#define FIRE_DISPLAY_SCALE 1
#define FIRE_DISPLAY_START_DRAG 1
#define FIRE_DISPLAY_CAN_START_DRAG 1

enum { FIRE_DD_NONE = 0, FIRE_DD_HEADLESS = 1, FIRE_DD_KIND_X11 = 2, FIRE_DD_KIND_WAYLAND = 3, FIRE_DD_KIND_WIN32 = 4, FIRE_DD_KIND_COCOA = 5, FIRE_DD_KIND_WEB = 6 };

/* Which display this program talks to (decided once). */
static int fire_display_kind(void) {
    static volatile int kind = -1;
    if (kind < 0) {
        const char* want = getenv("FIRE_DISPLAY");
        const char* sdl = getenv("SDL_VIDEODRIVER");
        int k = FIRE_DD_NONE;
        if ((want == NULL || want[0] == 0) && sdl != NULL) {
            if (strcmp(sdl, "dummy") == 0 || strcmp(sdl, "offscreen") == 0) want = "headless";
            else if (strcmp(sdl, "x11") == 0 || strcmp(sdl, "wayland") == 0 || strcmp(sdl, "windows") == 0 || strcmp(sdl, "cocoa") == 0) want = strcmp(sdl, "windows") == 0 ? "win32" : sdl;
        }
        if (want != NULL && strcmp(want, "headless") == 0) k = FIRE_DD_HEADLESS;
#ifdef FIRE_DD_WAYLAND
        else if (want != NULL && strcmp(want, "wayland") == 0) k = fire_dwl_available() ? FIRE_DD_KIND_WAYLAND : FIRE_DD_NONE;
#endif
#ifdef FIRE_DD_X11
        else if (want != NULL && strcmp(want, "x11") == 0) k = fire_dx11_available() ? FIRE_DD_KIND_X11 : FIRE_DD_NONE;
#endif
        else {
#if defined(FIRE_DD_WEB)
            k = FIRE_DD_KIND_WEB;
#elif defined(FIRE_DD_WIN32)
            k = FIRE_DD_KIND_WIN32;
#elif defined(FIRE_DD_COCOA)
            k = FIRE_DD_KIND_COCOA;
#else
#  ifdef FIRE_DD_WAYLAND
            if (k == FIRE_DD_NONE && fire_dwl_available()) k = FIRE_DD_KIND_WAYLAND;
#  endif
#  ifdef FIRE_DD_X11
            if (k == FIRE_DD_NONE && fire_dx11_available()) k = FIRE_DD_KIND_X11;
#  endif
#endif
        }
        kind = k;
    }
    return kind;
}

typedef struct FireDisplayWindow {
    int kind;
    int vsync, touchMouse;
    union {
        FireDhlWindow hl;
#ifdef FIRE_DD_X11
        FireDx11Window x11;
#endif
#ifdef FIRE_DD_WAYLAND
        FireDwlWindow wl;
#endif
#ifdef FIRE_DD_WIN32
        FireDw32Window w32;
#endif
#ifdef FIRE_DD_COCOA
        FireDnsWindow ns;
#endif
#ifdef FIRE_DD_WEB
        FireDwebWindow web;
#endif
    } u;
} FireDisplayWindow;

#ifdef FIRE_DD_X11
#  define FIRE_DD_IF_X11(x) x
#else
#  define FIRE_DD_IF_X11(x)
#endif
#ifdef FIRE_DD_WAYLAND
#  define FIRE_DD_IF_WL(x) x
#else
#  define FIRE_DD_IF_WL(x)
#endif
#ifdef FIRE_DD_WIN32
#  define FIRE_DD_IF_W32(x) x
#else
#  define FIRE_DD_IF_W32(x)
#endif
#ifdef FIRE_DD_COCOA
#  define FIRE_DD_IF_NS(x) x
#else
#  define FIRE_DD_IF_NS(x)
#endif
#ifdef FIRE_DD_WEB
#  define FIRE_DD_IF_WEB(x) x
#else
#  define FIRE_DD_IF_WEB(x)
#endif
/* `body(member, prefix)` once for every display that is built in */
#define FIRE_DD_SWITCH(kind, body) \
    switch (kind) { \
        case FIRE_DD_HEADLESS: body(hl, dhl) \
        FIRE_DD_IF_X11(case FIRE_DD_KIND_X11: body(x11, dx11)) \
        FIRE_DD_IF_WL(case FIRE_DD_KIND_WAYLAND: body(wl, dwl)) \
        FIRE_DD_IF_W32(case FIRE_DD_KIND_WIN32: body(w32, dw32)) \
        FIRE_DD_IF_NS(case FIRE_DD_KIND_COCOA: body(ns, dns)) \
        FIRE_DD_IF_WEB(case FIRE_DD_KIND_WEB: body(web, dweb)) \
        default: break; \
    }

static void fire_display_threadDone(void) {
#define FIRE_DD_B(m, p) fire_##p##_threadDone(); break;
    FIRE_DD_SWITCH(fire_display_kind(), FIRE_DD_B)
#undef FIRE_DD_B
}

static void fire_display_init(FireDisplayWindow* w) {
    memset(w, 0, sizeof *w);
    w->kind = fire_display_kind();
    w->vsync = 1;
    w->touchMouse = 1;
#define FIRE_DD_B(m, p) fire_##p##_init(&w->u.m); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}

static int fire_display_open(FireDisplayWindow* w, const char* title, int width, int height, int vsync) {
    w->vsync = vsync;
#define FIRE_DD_B(m, p) if (!fire_##p##_open(&w->u.m, title, width, height, vsync)) return 0; fire_##p##_setTouchMouse(&w->u.m, w->touchMouse); return 1;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
    return 0;
}

static void fire_display_close(FireDisplayWindow* w) {
#define FIRE_DD_B(m, p) fire_##p##_close(&w->u.m); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}

static void fire_display_setVSync(FireDisplayWindow* w, int v) {
    w->vsync = v;
#define FIRE_DD_B(m, p) fire_##p##_setVSync(&w->u.m, v); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}
static int fire_display_vsync(const FireDisplayWindow* w) { return w->vsync; }

static void fire_display_setSize(FireDisplayWindow* w, int width, int height) {
    if (width <= 0 || height <= 0) return;
#define FIRE_DD_B(m, p) fire_##p##_setSize(&w->u.m, width, height); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}

static void fire_display_setCursor(FireDisplayWindow* w, int c) {
    if (c < 0 || c >= FIRE_CURSOR_COUNT) return;
#define FIRE_DD_B(m, p) fire_##p##_setCursor(&w->u.m, c); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}

/* 1 (the default): a finger also makes mouse events; 0: only the touch events. */
static void fire_display_setTouchMouse(FireDisplayWindow* w, int v) {
    w->touchMouse = v;
#define FIRE_DD_B(m, p) fire_##p##_setTouchMouse(&w->u.m, v); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}
static int fire_display_touchMouse(const FireDisplayWindow* w) { return w->touchMouse; }

static void fire_display_setTextInputRect(FireDisplayWindow* w, int x, int y, int width, int height) {
#define FIRE_DD_B(m, p) fire_##p##_setTextInputRect(&w->u.m, x, y, width, height); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}

static int fire_display_setBackdrop(FireDisplayWindow* w, int kind) {
#define FIRE_DD_B(m, p) return fire_##p##_setBackdrop(&w->u.m, kind);
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
    return FIRE_BACKDROP_NONE;
}

static double fire_display_scale(FireDisplayWindow* w) {
#define FIRE_DD_B(m, p) return fire_##p##_scale(&w->u.m);
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
    return 1.0;
}

static int fire_display_pump(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    int open = 0;
#define FIRE_DD_B(m, p) open = fire_##p##_pump(&w->u.m, out, fbWidth, fbHeight); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
    /* the joysticks and gamepads of the program (fire_display_joystick.h): to the window that pumps */
    if (open) fire_joy_poll(out, w->kind != FIRE_DD_HEADLESS);
    return open;
}

/* Shows the pixels (R,G,B,A per 32-bit word, width*height), stretched to the window. */
static void fire_display_present(FireDisplayWindow* w, const uint32_t* rgba, int width, int height) {
#define FIRE_DD_B(m, p) fire_##p##_present(&w->u.m, rgba, width, height); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}

static int fire_display_native(FireDisplayWindow* w, fire_native_window* out) {
    memset(out, 0, sizeof *out);
#define FIRE_DD_B(m, p) if (!fire_##p##_native(&w->u.m, out)) return 0; out->vsync = w->vsync; return 1;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
    return 0;
}

static void fire_display_restore(FireDisplayWindow* w) {
#define FIRE_DD_B(m, p) fire_##p##_restore(&w->u.m); break;
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
}

static int fire_display_startDrag(FireDisplayWindow* w, const char* text, const char* files) {
#define FIRE_DD_B(m, p) return fire_##p##_startDrag(&w->u.m, text, files);
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
    return 0;
}
static int fire_display_canStartDrag(FireDisplayWindow* w) {
    if (w == NULL) return 0;
#define FIRE_DD_B(m, p) return fire_##p##_canStartDrag(&w->u.m);
    FIRE_DD_SWITCH(w->kind, FIRE_DD_B)
#undef FIRE_DD_B
    return 0;
}

/* ---- the clipboard of the desktop (of the display the program talks to; without one the program's own) ---- */
#define FIRE_DD_CLIP(ret, name, params, args, none) \
    static ret fire_display_##name params { \
        switch (fire_display_kind()) { \
            FIRE_DD_IF_X11(case FIRE_DD_KIND_X11: return fire_dx11_##name args;) \
            FIRE_DD_IF_WL(case FIRE_DD_KIND_WAYLAND: return fire_dwl_##name args;) \
            FIRE_DD_IF_W32(case FIRE_DD_KIND_WIN32: return fire_dw32_##name args;) \
            FIRE_DD_IF_NS(case FIRE_DD_KIND_COCOA: return fire_dns_##name args;) \
            FIRE_DD_IF_WEB(case FIRE_DD_KIND_WEB: return fire_dweb_##name args;) \
            default: return fire_dhl_##name args; \
        } \
        (void)0; none \
    }
FIRE_DD_CLIP(int, clipboardSet, (const char* text), (text), )
FIRE_DD_CLIP(char*, clipboardGet, (void), (), )
FIRE_DD_CLIP(int, clipboardHas, (void), (), )
FIRE_DD_CLIP(unsigned char*, clipboardGetImage, (size_t* size), (size), )
FIRE_DD_CLIP(int, clipboardSetImage, (const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize), (png, pngSize, bmp, bmpSize), )
FIRE_DD_CLIP(int, clipboardHasImage, (void), (), )
FIRE_DD_CLIP(char*, clipboardGetFiles, (void), (), )
FIRE_DD_CLIP(int, clipboardSetFiles, (const char* paths), (paths), )
FIRE_DD_CLIP(int, clipboardHasFiles, (void), (), )
#undef FIRE_DD_CLIP

#endif
