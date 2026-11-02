/* fire native platform package "std": a window on the desktop through SDL3 (link with -lSDL3; the generated program asks for it with a `// fire-link: SDL3` line and
 * defines FIRE_SDL3 - the build takes it when the toolchain can compile and link SDL3, else SDL2: fire_display_sdl.h, which this file follows event for event).
 * What it shows is the same as the VM's window (src/fire.Terminal.Sdl, also SDL3): the framebuffer as a streaming texture (R,G,B,A in memory = ABGR8888 on little
 * endian), stretched to the window, which can be resized; positions come back in framebuffer pixels; closing the window ends `Tick`. The interface is described in
 * fire_display_c.h. Beyond SDL2: what another program drags over the window (FIRE_EV_DRAG_HOVER, FIRE_EV_DRAG_LEAVE, the position of a drop) and pictures and
 * files on the clipboard (image/png, image/bmp, text/uri-list; on Windows SDL3 has CF_DIB for image/bmp, the files are CF_HDROP of this file). */
#ifndef FIRE_DISPLAY_SDL3_H
#define FIRE_DISPLAY_SDL3_H

#if defined(__TINYC__) && !defined(SDL_DISABLE_IMMINTRIN_H)
#  define SDL_DISABLE_IMMINTRIN_H 1   /* (TinyCC has no intrinsics headers) */
#endif
#include <SDL3/SDL.h>
#include <string.h>
#ifdef _WIN32
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif
#include "fire_display_c.h"
#include "fire_systheme.h"
#include "fire_dragout.h"

#define FIRE_DISPLAY_AVAILABLE 1

/* A thread of the program (not one of SDL's) that used SDL ends: SDL's thread-local data of it is freed (a window manager's thread, fire_bridge_windows.h). */
static void fire_display_threadDone(void) { SDL_CleanupTLS(); }

typedef struct FireDisplayWindow {
    SDL_Window* window;
    SDL_Cursor* cursors[12];
    int cursor;
    SDL_Renderer* renderer;
    SDL_Texture* texture;
    int winW, winH, texW, texH, fbWidth, fbHeight;
    SDL_WindowID windowId;
    int vsync, quit, ownsVideo, touchMouse, ownsJoystick;
    SDL_Joystick** joysticks;
    size_t joystickCount, joystickCap;
    /* a drop that is going on (SDL_EVENT_DROP_BEGIN .. SDL_EVENT_DROP_COMPLETE): the paths so far (one per line) and the text; whether a hover was reported */
    char* dropFiles;
    char* dropText;
    int dropping, hovering;
    float hoverX, hoverY;
    int direct;               /* a presenter draws into the window itself (fire_display_native): no renderer */
    SDL_MetalView metalView;  /* (macOS: the view with the layer the presenter draws into) */
} FireDisplayWindow;

/* One lock for everything that touches SDL's events: the pumping of the windows (from several threads: the managers) and the clipboard (on X11 SDL waits for
 * the answer of the clipboard's owner by pumping events itself). */
static SDL_Mutex* fire_display_sdlMutex(void) {
    static SDL_Mutex* volatile mutex = NULL;
    static SDL_SpinLock guard = 0;
    if (mutex == NULL) {
        SDL_LockSpinlock(&guard);
        if (mutex == NULL) mutex = SDL_CreateMutex();
        SDL_UnlockSpinlock(&guard);
    }
    return mutex;
}
static void fire_display_lockSdl(void) { SDL_Mutex* m = fire_display_sdlMutex(); if (m) SDL_LockMutex(m); }
static void fire_display_unlockSdl(void) { SDL_Mutex* m = fire_display_sdlMutex(); if (m) SDL_UnlockMutex(m); }

/* (appends a line to a text that grows: "a" + "b" = "a\nb") */
static char* fire_display_appendLine(char* all, const char* line) {
    size_t a = all ? strlen(all) : 0, b = strlen(line);
    char* grown = (char*)realloc(all, a + b + 2);
    if (grown == NULL) return all;
    if (a > 0) grown[a++] = '\n';
    memcpy(grown + a, line, b + 1);
    return grown;
}

static void fire_display_init(FireDisplayWindow* w) {
    memset(w, 0, sizeof *w);
    w->cursor = -1;
    w->vsync = 1;
    w->touchMouse = 1;
}

static void fire_display_close(FireDisplayWindow* w) {
    int k;
    size_t j;
    for (k = 0; k < 12; k++) if (w->cursors[k]) { SDL_DestroyCursor(w->cursors[k]); w->cursors[k] = NULL; }
    w->cursor = -1;
    if (w->texture) { SDL_DestroyTexture(w->texture); w->texture = NULL; }
    for (j = 0; j < w->joystickCount; j++) SDL_CloseJoystick(w->joysticks[j]);
    free(w->joysticks);
    w->joysticks = NULL; w->joystickCount = 0; w->joystickCap = 0;
    free(w->dropFiles); free(w->dropText);
    w->dropFiles = w->dropText = NULL; w->dropping = 0; w->hovering = 0;
    if (w->ownsJoystick) { SDL_QuitSubSystem(SDL_INIT_JOYSTICK); w->ownsJoystick = 0; }
    if (w->renderer) { SDL_DestroyRenderer(w->renderer); w->renderer = NULL; }
    if (w->metalView) { SDL_Metal_DestroyView(w->metalView); w->metalView = NULL; }
    w->direct = 0;
    if (w->window) { SDL_DestroyWindow(w->window); w->window = NULL; }
    if (w->ownsVideo) { SDL_QuitSubSystem(SDL_INIT_VIDEO); w->ownsVideo = 0; }
}

#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT) || defined(FIRE_DISPLAY_SELFTEST_DROP) || defined(FIRE_DISPLAY_SELFTEST_HOVER)
/* The tests (and nobody else) push a fixed series of events when a window opens: keys, a click, motion, the wheel, text and the close (the same as
 * fire_display_sdl.h pushes, so that both give the same output). */
static void fire_display_selftest(FireDisplayWindow* w) {
    SDL_Event e;
    (void)w;
#ifdef FIRE_DISPLAY_SELFTEST_RESIZE
    /* (only for the test of Window.AutoResize: the window is pulled to 90x70) */
    SDL_zero(e); e.type = SDL_EVENT_WINDOW_RESIZED; e.window.windowID = w->windowId; e.window.data1 = 90; e.window.data2 = 70; SDL_PushEvent(&e);
    return;
#endif
#ifdef FIRE_DISPLAY_SELFTEST_INPUT
    /* (only for the test of touch and joystick: a finger touches, moves and lifts; a joystick moves an axis, presses a button, moves the hat, is removed) */
    SDL_zero(e); e.type = SDL_EVENT_FINGER_DOWN; e.tfinger.fingerID = 5; e.tfinger.x = 0.5f; e.tfinger.y = 0.25f; e.tfinger.pressure = 1.0f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_FINGER_MOTION; e.tfinger.fingerID = 5; e.tfinger.x = 0.75f; e.tfinger.y = 0.5f; e.tfinger.pressure = 0.5f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_FINGER_UP; e.tfinger.fingerID = 5; e.tfinger.x = 0.75f; e.tfinger.y = 0.5f; e.tfinger.pressure = 0.0f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_JOYSTICK_AXIS_MOTION; e.jaxis.which = 7; e.jaxis.axis = 1; e.jaxis.value = -32768; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_JOYSTICK_BUTTON_DOWN; e.jbutton.which = 7; e.jbutton.button = 3; e.jbutton.down = true; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_JOYSTICK_BUTTON_UP; e.jbutton.which = 7; e.jbutton.button = 3; e.jbutton.down = false; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_JOYSTICK_HAT_MOTION; e.jhat.which = 7; e.jhat.hat = 0; e.jhat.value = SDL_HAT_UP | SDL_HAT_RIGHT; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_JOYSTICK_REMOVED; e.jdevice.which = 7; SDL_PushEvent(&e);
    return;
#endif
#ifdef FIRE_DISPLAY_SELFTEST_HOVER
    /* (only for the test of DragHover/DragLeave: something is dragged over the window and leaves it; then something is dragged over it and dropped) */
    SDL_zero(e); e.type = SDL_EVENT_DROP_BEGIN; e.drop.windowID = w->windowId; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_POSITION; e.drop.windowID = w->windowId; e.drop.x = 10; e.drop.y = 20; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_POSITION; e.drop.windowID = w->windowId; e.drop.x = 10; e.drop.y = 20; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_POSITION; e.drop.windowID = w->windowId; e.drop.x = 30; e.drop.y = 22; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_COMPLETE; e.drop.windowID = w->windowId; e.drop.x = 30; e.drop.y = 22; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_BEGIN; e.drop.windowID = w->windowId; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_POSITION; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_FILE; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; e.drop.data = "/tmp/hier.txt"; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_COMPLETE; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; SDL_PushEvent(&e);
    return;
#endif
    SDL_zero(e); e.type = SDL_EVENT_KEY_DOWN; e.key.key = 'a'; e.key.scancode = SDL_SCANCODE_A; e.key.mod = SDL_KMOD_LSHIFT; e.key.repeat = false; e.key.down = true; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_KEY_UP; e.key.key = 'a'; e.key.scancode = SDL_SCANCODE_A; e.key.mod = 0; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_MOUSE_BUTTON_DOWN; e.button.button = SDL_BUTTON_LEFT; e.button.down = true; e.button.x = 32; e.button.y = 24; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_MOUSE_BUTTON_UP; e.button.button = SDL_BUTTON_LEFT; e.button.x = 32; e.button.y = 24; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_MOUSE_MOTION; e.motion.x = 10; e.motion.y = 12; e.motion.xrel = 3; e.motion.yrel = -2; e.motion.state = 1; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_MOUSE_WHEEL; e.wheel.x = 0; e.wheel.y = 1.5f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_TEXT_INPUT; e.text.text = "\xC3\xA9x"; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_WINDOW_CLOSE_REQUESTED; e.window.windowID = w->windowId; SDL_PushEvent(&e);
#ifdef FIRE_DISPLAY_SELFTEST_DROP
    /* two files and a text are dropped at (40, 30) of the window; then a single file without begin/complete */
    SDL_zero(e); e.type = SDL_EVENT_DROP_BEGIN; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_FILE; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; e.drop.data = "/tmp/eins.txt"; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_FILE; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; e.drop.data = "/tmp/zwei \xC3\xA4.png"; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_TEXT; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; e.drop.data = "gezogener Text"; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_COMPLETE; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_EVENT_DROP_FILE; e.drop.windowID = w->windowId; e.drop.x = 40; e.drop.y = 30; e.drop.data = "/tmp/allein.txt"; SDL_PushEvent(&e);
#endif
    SDL_zero(e); e.type = SDL_EVENT_QUIT; SDL_PushEvent(&e);
}
#endif

static int fire_display_open(FireDisplayWindow* w, const char* title, int width, int height, int vsync) {
    w->vsync = vsync;
    w->winW = width; w->winH = height;
    /* every window holds a reference of its own (SDL counts them): closing one window must not shut the video down under the others */
    if (!SDL_InitSubSystem(SDL_INIT_VIDEO)) return 0;
    w->ownsVideo = 1;
    w->window = SDL_CreateWindow(title, width, height, SDL_WINDOW_RESIZABLE);
    if (!w->window) { fire_display_close(w); return 0; }
    w->renderer = SDL_CreateRenderer(w->window, NULL);
    if (!w->renderer) w->renderer = SDL_CreateRenderer(w->window, SDL_SOFTWARE_RENDERER);
    if (!w->renderer) { fire_display_close(w); return 0; }
    SDL_SetRenderVSync(w->renderer, vsync ? 1 : 0);
    SDL_StartTextInput(w->window);
    w->windowId = SDL_GetWindowID(w->window);
    /* joysticks are an extra: without a driver the window opens all the same (the devices that are plugged in report themselves as SDL_EVENT_JOYSTICK_ADDED)
     * (a reference of its own as well, see above) */
    if (SDL_InitSubSystem(SDL_INIT_JOYSTICK)) w->ownsJoystick = 1;
    SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, w->touchMouse ? "1" : "0");
    /* what the user drops onto the window: files and text, and where it is while it is dragged */
    SDL_SetEventEnabled(SDL_EVENT_DROP_FILE, true);
    SDL_SetEventEnabled(SDL_EVENT_DROP_TEXT, true);
    SDL_SetEventEnabled(SDL_EVENT_DROP_BEGIN, true);
    SDL_SetEventEnabled(SDL_EVENT_DROP_POSITION, true);
    SDL_SetEventEnabled(SDL_EVENT_DROP_COMPLETE, true);
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT) || defined(FIRE_DISPLAY_SELFTEST_DROP) || defined(FIRE_DISPLAY_SELFTEST_HOVER)
    fire_display_selftest(w);
#endif
    return 1;
}

static void fire_display_setVSync(FireDisplayWindow* w, int v) { w->vsync = v; if (w->renderer) SDL_SetRenderVSync(w->renderer, v ? 1 : 0); }
/* A new size of the window in pixels of the screen (the program asks for it: no resize event comes back). */
static void fire_display_setSize(FireDisplayWindow* w, int width, int height) { if (w->window && width > 0 && height > 0) { SDL_SetWindowSize(w->window, width, height); w->winW = width; w->winH = height; } }
/* The mouse pointer over the window: a system cursor (SDL_SystemCursor: 0 arrow, 1 text, ... 11 hand - the same numbers as in SDL2), made once per kind. */
static void fire_display_setCursor(FireDisplayWindow* w, int c) {
    if (!w->window || c < 0 || c >= 12 || c == w->cursor) return;
    if (!w->cursors[c]) w->cursors[c] = SDL_CreateSystemCursor((SDL_SystemCursor)c);
    if (!w->cursors[c]) return;
    SDL_SetCursor(w->cursors[c]);
    w->cursor = c;
}
static int fire_display_vsync(const FireDisplayWindow* w) { return w->vsync; }
/* 1 (the default): a finger also makes mouse events (as SDL does itself); 0: only the touch events. */
static void fire_display_setTouchMouse(FireDisplayWindow* w, int v) { w->touchMouse = v; SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, v ? "1" : "0"); }
static int fire_display_touchMouse(const FireDisplayWindow* w) { return w->touchMouse; }

static void fire_display_toFramebuffer(const FireDisplayWindow* w, float x, float y, float* ox, float* oy) {
    int ww = 0, hh = 0;
    if (w->window) SDL_GetWindowSize(w->window, &ww, &hh);
    if (ww <= 0 || hh <= 0 || w->fbWidth <= 0) { *ox = x; *oy = y; return; }
    *ox = x * (float)w->fbWidth / (float)ww; *oy = y * (float)w->fbHeight / (float)hh;
}

/* The drop that was collected goes out (SDL3 says where it landed, in pixels of the window): a DropFiles event with the paths, a DropText event with the text;
 * a drag that brought nothing (it left the window) ends with DragLeave. */
static void fire_display_dropDone(FireDisplayWindow* w, FireDisplayEvents* out, float wx, float wy) {
    FireDisplayEvent e;
    float x, y;
    fire_display_toFramebuffer(w, wx, wy, &x, &y);
    if (w->dropFiles != NULL) {
        fire_display_eventInit(&e);
        e.type = FIRE_EV_DROP_FILES; e.x = x; e.y = y; e.data = w->dropFiles;
        fire_display_push(out, &e);
    }
    if (w->dropText != NULL) {
        fire_display_eventInit(&e);
        e.type = FIRE_EV_DROP_TEXT; e.x = x; e.y = y; e.data = w->dropText;
        fire_display_push(out, &e);
    }
    if (w->dropFiles == NULL && w->dropText == NULL && w->hovering) {
        fire_display_eventInit(&e);
        e.type = FIRE_EV_DRAG_LEAVE;
        fire_display_push(out, &e);
    }
    free(w->dropFiles); free(w->dropText);
    w->dropFiles = w->dropText = NULL;
    w->dropping = 0;
    w->hovering = 0;
}

/* Takes the pending events of this window. 0 once the window was closed. */
static int fire_display_pump(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    SDL_Event ev;
    w->fbWidth = fbWidth; w->fbHeight = fbHeight;
    fire_display_lockSdl();
    while (SDL_PollEvent(&ev)) {
        FireDisplayEvent e;
        fire_display_eventInit(&e);
        switch (ev.type) {
            case SDL_EVENT_QUIT: e.type = FIRE_EV_CLOSE; fire_display_push(out, &e); w->quit = 1; break;
            case SDL_EVENT_WINDOW_CLOSE_REQUESTED: e.type = FIRE_EV_CLOSE_REQUEST; fire_display_push(out, &e); break;
            case SDL_EVENT_WINDOW_RESIZED:
                if (ev.window.windowID == w->windowId && (ev.window.data1 != w->winW || ev.window.data2 != w->winH)) {
                    w->winW = ev.window.data1; w->winH = ev.window.data2;
                    e.type = FIRE_EV_RESIZE; e.width = w->winW; e.height = w->winH; fire_display_push(out, &e);
                }
                break;
            case SDL_EVENT_KEY_DOWN: case SDL_EVENT_KEY_UP:
                e.type = ev.type == SDL_EVENT_KEY_DOWN ? FIRE_EV_KEY_DOWN : FIRE_EV_KEY_UP;
                e.keyCode = (int)ev.key.key; e.scanCode = (int)ev.key.scancode; e.modifier = (int)ev.key.mod; e.repeat = ev.key.repeat ? 1 : 0;
                fire_display_push(out, &e);
                break;
            case SDL_EVENT_MOUSE_BUTTON_DOWN: case SDL_EVENT_MOUSE_BUTTON_UP:
                e.type = ev.type == SDL_EVENT_MOUSE_BUTTON_DOWN ? FIRE_EV_MOUSE_DOWN : FIRE_EV_MOUSE_UP;
                e.button = ev.button.button;
                fire_display_toFramebuffer(w, ev.button.x, ev.button.y, &e.x, &e.y);
                fire_display_push(out, &e);
                break;
            case SDL_EVENT_MOUSE_MOTION: {
                FireDisplayEvent r;
                float ox, oy, mx, my;
                e.type = FIRE_EV_MOUSE_MOVE; e.buttonState = (int)ev.motion.state;
                fire_display_toFramebuffer(w, ev.motion.x, ev.motion.y, &e.x, &e.y);
                fire_display_push(out, &e);
                fire_display_eventInit(&r);
                r.type = FIRE_EV_MOUSE_MOVE_REL; r.buttonState = (int)ev.motion.state;
                fire_display_toFramebuffer(w, 0, 0, &ox, &oy); fire_display_toFramebuffer(w, ev.motion.xrel, ev.motion.yrel, &mx, &my);
                r.xrel = mx - ox; r.yrel = my - oy;
                fire_display_push(out, &r);
                break;
            }
            case SDL_EVENT_MOUSE_WHEEL:
                e.type = FIRE_EV_MOUSE_SCROLL; e.scrollX = ev.wheel.x; e.scrollY = ev.wheel.y;
                fire_display_toFramebuffer(w, ev.wheel.mouse_x, ev.wheel.mouse_y, &e.x, &e.y);
                fire_display_push(out, &e);
                break;
            case SDL_EVENT_FINGER_DOWN: case SDL_EVENT_FINGER_MOTION: case SDL_EVENT_FINGER_UP:
                /* SDL reports the position normalized (0 to 1 over the window): in framebuffer pixels it is simply x * width */
                e.type = ev.type == SDL_EVENT_FINGER_DOWN ? FIRE_EV_TOUCH_DOWN : ev.type == SDL_EVENT_FINGER_UP ? FIRE_EV_TOUCH_UP : FIRE_EV_TOUCH_MOVE;
                e.finger = (long long)ev.tfinger.fingerID; e.x = ev.tfinger.x * (float)w->fbWidth; e.y = ev.tfinger.y * (float)w->fbHeight; e.pressure = ev.tfinger.pressure;
                fire_display_push(out, &e);
                break;
            case SDL_EVENT_JOYSTICK_ADDED: {
                SDL_Joystick* js = SDL_OpenJoystick(ev.jdevice.which);
                e.type = FIRE_EV_JOY_ADDED;
                e.joystick = (int)ev.jdevice.which;
                if (js) {
                    if (w->joystickCount == w->joystickCap) {
                        size_t cap = w->joystickCap ? w->joystickCap * 2 : 4;
                        SDL_Joystick** grown = (SDL_Joystick**)realloc(w->joysticks, sizeof(SDL_Joystick*) * cap);
                        if (grown != NULL) { w->joysticks = grown; w->joystickCap = cap; }
                    }
                    if (w->joystickCount < w->joystickCap) w->joysticks[w->joystickCount++] = js; else SDL_CloseJoystick(js);
                }
                fire_display_push(out, &e);
                break;
            }
            case SDL_EVENT_JOYSTICK_REMOVED: {
                size_t k;
                for (k = 0; k < w->joystickCount; k++)
                    if (SDL_GetJoystickID(w->joysticks[k]) == ev.jdevice.which) {
                        SDL_CloseJoystick(w->joysticks[k]);
                        memmove(w->joysticks + k, w->joysticks + k + 1, sizeof(SDL_Joystick*) * (w->joystickCount - k - 1));
                        w->joystickCount--;
                        break;
                    }
                e.type = FIRE_EV_JOY_REMOVED; e.joystick = (int)ev.jdevice.which;
                fire_display_push(out, &e);
                break;
            }
            case SDL_EVENT_JOYSTICK_AXIS_MOTION: {
                float v = (float)ev.jaxis.value / 32767.0f;
                e.type = FIRE_EV_JOY_AXIS; e.joystick = (int)ev.jaxis.which; e.index = ev.jaxis.axis;
                e.value = v < -1.0f ? -1.0f : v;   /* 16 bit, -32768 to 32767 -> -1 to 1 */
                fire_display_push(out, &e);
                break;
            }
            case SDL_EVENT_JOYSTICK_BUTTON_DOWN: case SDL_EVENT_JOYSTICK_BUTTON_UP:
                e.type = ev.type == SDL_EVENT_JOYSTICK_BUTTON_DOWN ? FIRE_EV_JOY_BUTTON_DOWN : FIRE_EV_JOY_BUTTON_UP; e.joystick = (int)ev.jbutton.which; e.index = ev.jbutton.button;
                fire_display_push(out, &e);
                break;
            case SDL_EVENT_JOYSTICK_HAT_MOTION:
                e.type = FIRE_EV_JOY_HAT; e.joystick = (int)ev.jhat.which; e.index = ev.jhat.hat; e.value = (float)ev.jhat.value;
                fire_display_push(out, &e);
                break;
            case SDL_EVENT_TEXT_INPUT: {
                size_t n = ev.text.text ? strlen(ev.text.text) : 0;
                if (n > sizeof e.text - 1) n = sizeof e.text - 1;
                e.type = FIRE_EV_TEXT_INPUT;
                if (n) memcpy(e.text, ev.text.text, n);
                e.text[n] = 0;
                fire_display_push(out, &e);
                break;
            }
            /* (the strings of the drop events belong to SDL) */
            case SDL_EVENT_DROP_BEGIN: w->dropping = 1; break;
            case SDL_EVENT_DROP_POSITION:
                /* something of another program is over the window: where (only when it moved) */
                w->dropping = 1;
                if (!w->hovering || ev.drop.x != w->hoverX || ev.drop.y != w->hoverY) {
                    w->hovering = 1; w->hoverX = ev.drop.x; w->hoverY = ev.drop.y;
                    e.type = FIRE_EV_DRAG_HOVER;
                    fire_display_toFramebuffer(w, ev.drop.x, ev.drop.y, &e.x, &e.y);
                    fire_display_push(out, &e);
                }
                break;
            case SDL_EVENT_DROP_TEXT:
                if (ev.drop.data) w->dropText = fire_display_appendLine(w->dropText, ev.drop.data);
                if (!w->dropping) fire_display_dropDone(w, out, ev.drop.x, ev.drop.y);
                break;
            case SDL_EVENT_DROP_FILE:
                if (ev.drop.data) w->dropFiles = fire_display_appendLine(w->dropFiles, ev.drop.data);
                if (!w->dropping) fire_display_dropDone(w, out, ev.drop.x, ev.drop.y);   /* (a file without begin and complete is a drop of its own) */
                break;
            case SDL_EVENT_DROP_COMPLETE: fire_display_dropDone(w, out, ev.drop.x, ev.drop.y); break;
            default: break;
        }
    }
    fire_display_unlockSdl();
    return !w->quit;
}

/* ---- the clipboard of the desktop: text, pictures, files ---- */
/* The video part of SDL must run for the clipboard (on X11 it needs a window of SDL's own): it is started once and stays. */
static int fire_display_clipboardReady(void) {
    static int ready = 0;
    if (!ready) {
        if (!SDL_InitSubSystem(SDL_INIT_VIDEO)) return 0;
        ready = 1;
    }
    /* the events of the clipboard are taken in: what other programs did with it (X11: the owner changed, then the kinds it offers) before it is read, and their
     * questions for what this program put there (a program without a window answers here) */
    SDL_PumpEvents();
    return 1;
}
static int fire_display_clipboardSet(const char* text) {
    int ok;
    fire_display_lockSdl();
    ok = fire_display_clipboardReady() && SDL_SetClipboardText(text);
    fire_display_unlockSdl();
    return ok;
}
static char* fire_display_clipboardGet(void) {
    char* text = NULL;
    fire_display_lockSdl();
    if (fire_display_clipboardReady() && SDL_HasClipboardText()) {
        char* sdl = SDL_GetClipboardText();
        if (sdl != NULL) {
            if (sdl[0] != 0) text = (char*)fire_display_copyBytes(sdl, strlen(sdl));
            SDL_free(sdl);
        }
    }
    fire_display_unlockSdl();
    return text;
}
static int fire_display_clipboardHas(void) {
    int has;
    fire_display_lockSdl();
    has = fire_display_clipboardReady() && SDL_HasClipboardText();
    fire_display_unlockSdl();
    return has;
}

/* What the program offers on the clipboard (SDL asks the callback for it, when another program - or this one - wants it); it lives until SDL cleans it up. */
typedef struct FireDisplayOffer {
    unsigned char* png; size_t pngSize;
    unsigned char* bmp; size_t bmpSize;
    char* uris;            /* text/uri-list */
    char* gnome;           /* x-special/gnome-copied-files: "copy" and the URIs (what GNOME's file manager takes) */
} FireDisplayOffer;
static const void* SDLCALL fire_display_offerData(void* userdata, const char* mimeType, size_t* size) {
    const FireDisplayOffer* o = (const FireDisplayOffer*)userdata;
    *size = 0;
    if (o == NULL || mimeType == NULL) return NULL;
    if (strcmp(mimeType, "image/png") == 0 && o->png) { *size = o->pngSize; return o->png; }
    if (strcmp(mimeType, "image/bmp") == 0 && o->bmp) { *size = o->bmpSize; return o->bmp; }
    if (strcmp(mimeType, "text/uri-list") == 0 && o->uris) { *size = strlen(o->uris); return o->uris; }
    if (strcmp(mimeType, "x-special/gnome-copied-files") == 0 && o->gnome) { *size = strlen(o->gnome); return o->gnome; }
    return NULL;
}
static void SDLCALL fire_display_offerFree(void* userdata) {
    FireDisplayOffer* o = (FireDisplayOffer*)userdata;
    if (o == NULL) return;
    free(o->png); free(o->bmp); free(o->uris); free(o->gnome);
    free(o);
}

/* Reads a kind from the clipboard (malloc'd copy with a terminating 0; NULL: not there or empty). The caller holds the lock. */
static unsigned char* fire_display_clipboardData(const char* mime, size_t* size) {
    size_t n = 0;
    void* data;
    unsigned char* copy = NULL;
    *size = 0;
    if (!SDL_HasClipboardData(mime)) return NULL;
    data = SDL_GetClipboardData(mime, &n);
    if (data != NULL && n > 0) { copy = fire_display_copyBytes(data, n); if (copy) *size = n; }
    SDL_free(data);
    return copy;
}

static unsigned char* fire_display_clipboardGetImage(size_t* size) {
    unsigned char* image = NULL;
    *size = 0;
    fire_display_lockSdl();
    if (fire_display_clipboardReady()) {
        image = fire_display_clipboardData("image/png", size);
        if (image == NULL) image = fire_display_clipboardData("image/bmp", size);   /* (Windows: CF_DIB) */
    }
    fire_display_unlockSdl();
    return image;
}
static int fire_display_clipboardHasImage(void) {
    int has;
    fire_display_lockSdl();
    has = fire_display_clipboardReady() && (SDL_HasClipboardData("image/png") || SDL_HasClipboardData("image/bmp"));
    fire_display_unlockSdl();
    return has;
}
/* The picture as image/png and image/bmp (SDL3 on Windows puts image/bmp there as CF_DIB; on X11 both are offered). */
static int fire_display_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) {
    const char* mimes[2];
    size_t count = 0;
    int ok;
    FireDisplayOffer* o;
    if (png == NULL || pngSize == 0) return 0;
    o = (FireDisplayOffer*)calloc(1, sizeof *o);
    if (o == NULL) return 0;
    o->png = fire_display_copyBytes(png, pngSize); o->pngSize = pngSize;
    if (bmp != NULL && bmpSize > 0) { o->bmp = fire_display_copyBytes(bmp, bmpSize); o->bmpSize = bmpSize; }
    if (o->png == NULL || (bmp != NULL && bmpSize > 0 && o->bmp == NULL)) { fire_display_offerFree(o); return 0; }
    mimes[count++] = "image/png";
    if (o->bmp) mimes[count++] = "image/bmp";
    fire_display_lockSdl();
    if (!fire_display_clipboardReady()) { fire_display_unlockSdl(); fire_display_offerFree(o); return 0; }
    ok = SDL_SetClipboardData(fire_display_offerData, fire_display_offerFree, o, mimes, count);   /* (on failure SDL has already cleaned `o` up) */
    fire_display_unlockSdl();
    return ok;
}

#ifdef _WIN32
/* Files on the Windows clipboard are CF_HDROP (SDL3 does not have it): a DROPFILES header and the paths, each ending in 0, the list ending in another 0. Only
 * with SDL's video driver of Windows (with another one - the dummy of the tests - the clipboard is SDL's own). */
static int fire_display_winClipboard(void) { const char* driver = SDL_GetCurrentVideoDriver(); return driver != NULL && strcmp(driver, "windows") == 0; }
static HWND fire_display_clipboardOwner(void) {
    static HWND owner = NULL;   /* a message-only window: the clipboard needs an owner for SetClipboardData */
    if (owner == NULL) owner = CreateWindowExW(0, L"STATIC", L"fire clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, NULL, NULL, NULL);
    return owner;
}
static int fire_display_openClipboard(HWND owner) {
    int attempt;
    for (attempt = 0; attempt < 5; attempt++) { if (OpenClipboard(owner)) return 1; Sleep(10); }
    return 0;
}
static char* fire_display_winGetFiles(void) {
    char* paths = NULL;
    HANDLE mem;
    const unsigned char* drop;
    if (!IsClipboardFormatAvailable(CF_HDROP) || !fire_display_openClipboard(NULL)) return NULL;
    mem = GetClipboardData(CF_HDROP);
    drop = mem ? (const unsigned char*)GlobalLock(mem) : NULL;
    if (drop != NULL) {
        SIZE_T total = GlobalSize(mem);
        DWORD offset = *(const DWORD*)drop;           /* DROPFILES.pFiles */
        BOOL wide = *(const BOOL*)(drop + 16);        /* DROPFILES.fWide (after pFiles, pt, fNC) */
        if (offset >= 20 && offset < total) {
            if (wide) {
                const WCHAR* p = (const WCHAR*)(drop + offset);
                const WCHAR* end = (const WCHAR*)(drop + total);
                while (p < end && *p) {
                    int len = 0, bytes;
                    while (p + len < end && p[len]) len++;
                    bytes = WideCharToMultiByte(CP_UTF8, 0, p, len, NULL, 0, NULL, NULL);
                    if (bytes > 0) {
                        char* line = (char*)malloc((size_t)bytes + 1);
                        if (line) {
                            WideCharToMultiByte(CP_UTF8, 0, p, len, line, bytes, NULL, NULL);
                            line[bytes] = 0;
                            paths = fire_display_appendLine(paths, line);
                            free(line);
                        }
                    }
                    p += len + 1;
                }
            } else {
                const char* p = (const char*)(drop + offset);
                const char* end = (const char*)(drop + total);
                while (p < end && *p) {
                    size_t len = strnlen(p, (size_t)(end - p));
                    char* line = (char*)fire_display_copyBytes(p, len);
                    if (line) { paths = fire_display_appendLine(paths, line); free(line); }
                    p += len + 1;
                }
            }
        }
        GlobalUnlock(mem);
    }
    CloseClipboard();
    return paths;
}
static int fire_display_winSetFiles(const char* paths) {
    size_t n = strlen(paths), i;
    int wideCount, ok = 0;
    WCHAR* wide;
    HGLOBAL mem;
    unsigned char* drop;
    HWND owner = fire_display_clipboardOwner();
    wideCount = MultiByteToWideChar(CP_UTF8, 0, paths, (int)n, NULL, 0);
    if (wideCount < 0) return 0;
    mem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, 20 + ((size_t)wideCount + 2) * sizeof(WCHAR));
    if (mem == NULL) return 0;
    drop = (unsigned char*)GlobalLock(mem);
    if (drop == NULL) { GlobalFree(mem); return 0; }
    *(DWORD*)drop = 20;          /* pFiles */
    *(BOOL*)(drop + 16) = TRUE;  /* fWide */
    wide = (WCHAR*)(drop + 20);
    if (wideCount > 0) MultiByteToWideChar(CP_UTF8, 0, paths, (int)n, wide, wideCount);
    for (i = 0; i < (size_t)wideCount; i++) if (wide[i] == L'\n') wide[i] = 0;   /* one path per line -> each ends in 0; the list ends in two */
    GlobalUnlock(mem);
    if (fire_display_openClipboard(owner)) {
        EmptyClipboard();
        ok = SetClipboardData(CF_HDROP, mem) != NULL;
        CloseClipboard();
    }
    if (!ok) GlobalFree(mem);
    return ok;
}
#endif

static char* fire_display_clipboardGetFiles(void) {
    char* paths = NULL;
    fire_display_lockSdl();
    if (fire_display_clipboardReady()) {
#ifdef _WIN32
        if (fire_display_winClipboard()) paths = fire_display_winGetFiles();
#endif
        if (paths == NULL) {
            size_t n = 0;
            unsigned char* list = fire_display_clipboardData("text/uri-list", &n);
            if (list != NULL) { paths = fire_display_pathsFromUriList((const char*)list, n); free(list); }
        }
        if (paths == NULL) {
            /* GNOME's file manager: "copy" or "cut", then the URIs */
            size_t n = 0;
            unsigned char* list = fire_display_clipboardData("x-special/gnome-copied-files", &n);
            if (list != NULL) { paths = fire_display_pathsFromUriList((const char*)list, n); free(list); }
        }
    }
    fire_display_unlockSdl();
    return paths;
}
static int fire_display_clipboardHasFiles(void) {
    char* paths = fire_display_clipboardGetFiles();
    int has = paths != NULL;
    free(paths);
    return has;
}
/* The paths (one per line) as text/uri-list (and for GNOME's file manager x-special/gnome-copied-files); on Windows as CF_HDROP. */
static int fire_display_clipboardSetFiles(const char* paths) {
    const char* mimes[2];
    FireDisplayOffer* o;
    int ok;
    if (paths == NULL || paths[0] == 0) return 0;
#ifdef _WIN32
    fire_display_lockSdl();
    /* (SDL's own offer is withdrawn first: it would still answer for the kinds it does not know on Windows, e.g. image/png) */
    ok = fire_display_clipboardReady() && fire_display_winClipboard() && SDL_ClearClipboardData() && fire_display_winSetFiles(paths);
    fire_display_unlockSdl();
    if (ok) return 1;
#endif
    o = (FireDisplayOffer*)calloc(1, sizeof *o);
    if (o == NULL) return 0;
    o->uris = fire_display_uriListFromPaths(paths);
    if (o->uris != NULL) {
        size_t n = strlen(o->uris);
        o->gnome = (char*)malloc(n + 6);
        if (o->gnome != NULL) { memcpy(o->gnome, "copy\n", 5); memcpy(o->gnome + 5, o->uris, n + 1); }
    }
    if (o->uris == NULL || o->gnome == NULL) { fire_display_offerFree(o); return 0; }
    mimes[0] = "text/uri-list";
    mimes[1] = "x-special/gnome-copied-files";
    fire_display_lockSdl();
    if (!fire_display_clipboardReady()) { fire_display_unlockSdl(); fire_display_offerFree(o); return 0; }
    ok = SDL_SetClipboardData(fire_display_offerData, fire_display_offerFree, o, mimes, 2);
    fire_display_unlockSdl();
    return ok;
}

/* Shows the pixels (R,G,B,A per 32-bit word, width*height), stretched to the window. */
static void fire_display_present(FireDisplayWindow* w, const uint32_t* rgba, int width, int height) {
    if (!w->renderer) return;
    if (!w->texture || w->texW != width || w->texH != height) {
        if (w->texture) SDL_DestroyTexture(w->texture);
        w->texture = SDL_CreateTexture(w->renderer, SDL_PIXELFORMAT_ABGR8888, SDL_TEXTUREACCESS_STREAMING, width, height);
        w->texW = width; w->texH = height;
    }
    if (!w->texture) return;
    SDL_UpdateTexture(w->texture, NULL, rgba, width * (int)sizeof(uint32_t));
    SDL_RenderClear(w->renderer);
    SDL_RenderTexture(w->renderer, w->texture, NULL, NULL);
    SDL_RenderPresent(w->renderer);
}

/* ---- a presenter draws into the window itself (fire_display_c.h: fire_display_native) ----
 * The native handles are properties of the SDL window (no header of a window system is needed for them). */
static int fire_display_native(FireDisplayWindow* w, fire_native_window* out) {
    SDL_PropertiesID props;
    const char* driver;
    int pw = 0, ph = 0;
    memset(out, 0, sizeof *out);
    if (!w->window) return 0;
    props = SDL_GetWindowProperties(w->window);
    driver = SDL_GetCurrentVideoDriver();
    if (driver == NULL) return 0;
    if (strcmp(driver, "windows") == 0) {
        out->kind = FIRE_NATIVE_WIN32;
        out->a = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WIN32_INSTANCE_POINTER, NULL);
        out->b = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WIN32_HWND_POINTER, NULL);
        if (out->b == NULL) return 0;
    } else if (strcmp(driver, "x11") == 0) {
        out->kind = FIRE_NATIVE_X11;
        out->a = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, NULL);
        out->id = (uint64_t)SDL_GetNumberProperty(props, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
        if (out->a == NULL || out->id == 0) return 0;
    } else if (strcmp(driver, "wayland") == 0) {
        out->kind = FIRE_NATIVE_WAYLAND;
        out->a = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, NULL);
        out->b = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, NULL);
        if (out->a == NULL || out->b == NULL) return 0;
    } else if (strcmp(driver, "cocoa") == 0) {
        if (!w->metalView) w->metalView = SDL_Metal_CreateView(w->window);
        if (!w->metalView) return 0;
        out->kind = FIRE_NATIVE_METAL;
        out->b = SDL_Metal_GetLayer(w->metalView);
    } else return 0;
    /* the window is the presenter's now: what showed the framebuffers goes (a swap chain of another API on the window would stand in the way) */
    if (!w->direct) {
        if (w->texture) { SDL_DestroyTexture(w->texture); w->texture = NULL; }
        if (w->renderer) { SDL_DestroyRenderer(w->renderer); w->renderer = NULL; }
        w->direct = 1;
    }
    SDL_GetWindowSizeInPixels(w->window, &pw, &ph);
    out->width = pw; out->height = ph;
    out->vsync = w->vsync;
    return 1;
}

static void fire_display_restore(FireDisplayWindow* w) {
    if (!w->direct) return;
    w->direct = 0;
    if (w->metalView) { SDL_Metal_DestroyView(w->metalView); w->metalView = NULL; }
    if (!w->renderer && w->window) {
        w->renderer = SDL_CreateRenderer(w->window, NULL);
        if (!w->renderer) w->renderer = SDL_CreateRenderer(w->window, SDL_SOFTWARE_RENDERER);
        if (w->renderer) SDL_SetRenderVSync(w->renderer, w->vsync ? 1 : 0);
    }
}

/* ---- dragging text and files out of the window into other programs (Window.StartDrag; SDL3 cannot start such a drag: fire_dragout.h does it for the
 * window of the platform) - runs until the user lets go: 0 nothing, 1 copied, 2 moved. Windows: OLE (the HWND of the window); X11: XDND (the Display and the
 * Window of SDL, with the lock of SDL's events held, so that nobody else takes the events of the connection meanwhile); elsewhere (Wayland, macOS) 0. ---- */
#define FIRE_DISPLAY_START_DRAG 1
static int fire_display_startDrag(FireDisplayWindow* w, const char* text, const char* files) {
    int result = 0;
    if (!w->window) return 0;
#if defined(_WIN32)
    {
        HWND hwnd = (HWND)SDL_GetPointerProperty(SDL_GetWindowProperties(w->window), SDL_PROP_WINDOW_WIN32_HWND_POINTER, NULL);
        if (hwnd != NULL) result = fire_dragout_win32(hwnd, text, files);
    }
#elif defined(FIRE_DRAGOUT_X11)
    {
        const char* driver = SDL_GetCurrentVideoDriver();
        if (driver != NULL && strcmp(driver, "x11") == 0) {
            SDL_PropertiesID props = SDL_GetWindowProperties(w->window);
            void* display = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, NULL);
            Sint64 xwindow = SDL_GetNumberProperty(props, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
            if (display != NULL && xwindow != 0) {
                fire_display_lockSdl();
                result = fire_dragout_x11(display, (unsigned long)xwindow, text, files);
                fire_display_unlockSdl();
            }
        }
    }
#else
    (void)text; (void)files;
#endif
    return result;
}

/* Can fire_display_startDrag hand a drag to the desktop here? Windows (the windows video driver) and X11 (the x11 driver, with fire_dragout.h's X11 part);
 * Wayland, macOS, the dummy/offscreen drivers: no. (Only the video driver is asked: callable from any thread.) */
#define FIRE_DISPLAY_CAN_START_DRAG 1
static int fire_display_canStartDrag(FireDisplayWindow* w) {
    if (w == NULL || !w->window) return 0;
#if defined(_WIN32) || defined(FIRE_DRAGOUT_X11)
    {
        const char* driver = SDL_GetCurrentVideoDriver();
#if defined(_WIN32)
        return driver != NULL && strcmp(driver, "windows") == 0;
#else
        return driver != NULL && strcmp(driver, "x11") == 0;
#endif
    }
#else
    return 0;
#endif
}

#endif
