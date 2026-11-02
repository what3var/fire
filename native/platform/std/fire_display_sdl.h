/* fire native platform package "std": a window on the desktop through SDL2 (link with -lSDL2; the generated program asks for it with a `// fire-link: SDL2` line).
 * The build takes SDL3 instead (fire_display_sdl3.h) when the toolchain can compile and link it; this one has text only on its clipboard and no hover events.
 * What it shows is the same as the VM's window (src/fire.Terminal.Sdl): the framebuffer as a streaming texture (R,G,B,A in memory = ABGR8888 on little endian),
 * stretched to the window, which can be resized; positions come back in framebuffer pixels; closing the window ends `Tick`. The interface is described in fire_display_c.h. */
#ifndef FIRE_DISPLAY_SDL_H
#define FIRE_DISPLAY_SDL_H

#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#endif
/* the program has its own main(): SDL must not rename it to SDL_main (that would need SDL2main and -lmingw32 on Windows) */
#ifndef SDL_MAIN_HANDLED
#  define SDL_MAIN_HANDLED
#endif
#if defined(__TINYC__) && !defined(SDL_DISABLE_IMMINTRIN_H)
#  define SDL_DISABLE_IMMINTRIN_H 1   /* (TinyCC has no intrinsics headers) */
#endif
#if defined(__has_include)
#  if __has_include(<SDL2/SDL.h>)
#    include <SDL2/SDL.h>
#  else
#    include <SDL.h>
#  endif
#elif defined(__TINYC__) && !defined(_WIN32) && !defined(FIRE_SDL_PLAIN_INCLUDE)
#  include <SDL2/SDL.h>   /* (TinyCC has no __has_include: the layout of the Linux distributions; FIRE_SDL_PLAIN_INCLUDE for <SDL.h> on an include path) */
#else
#  include <SDL.h>
#endif
#include <string.h>
#include "fire_display_c.h"
#include "fire_systheme.h"

#define FIRE_DISPLAY_AVAILABLE 1

/* A thread of the program (not one of SDL's) that used SDL ends: SDL's thread-local data of it is freed (a window manager's thread, fire_bridge_windows.h). */
static void fire_display_threadDone(void) {
#if SDL_VERSION_ATLEAST(2, 0, 16)
    SDL_TLSCleanup();
#endif
}

typedef struct FireDisplayWindow {
    SDL_Window* window;
    SDL_Cursor* cursors[12];
    int cursor;
    SDL_Renderer* renderer;
    SDL_Texture* texture;
    int winW, winH, texW, texH, fbWidth, fbHeight;
    Uint32 windowId;
    int vsync, quit, ownsVideo, touchMouse, ownsJoystick;
    SDL_Joystick** joysticks;
    size_t joystickCount, joystickCap;
    int direct;          /* a presenter draws into the window itself (fire_display_native): no renderer */
    void* metalView;     /* (macOS: the view with the layer the presenter draws into) */
    /* a drop that is going on (SDL_DROPBEGIN .. SDL_DROPCOMPLETE): the paths so far (one per line) and the text */
    char* dropFiles;
    char* dropText;
    int dropping;
} FireDisplayWindow;

/* One lock for everything that touches SDL's events: the pumping of the windows (from several threads: the managers) and the clipboard (on X11 SDL waits for
 * the answer of the clipboard's owner by pumping events itself). */
static SDL_mutex* fire_display_sdlMutex(void) {
    static SDL_mutex* volatile mutex = NULL;
    static SDL_SpinLock guard = 0;
    if (mutex == NULL) {
        SDL_AtomicLock(&guard);
        if (mutex == NULL) mutex = SDL_CreateMutex();
        SDL_AtomicUnlock(&guard);
    }
    return mutex;
}
static void fire_display_lockSdl(void) { SDL_mutex* m = fire_display_sdlMutex(); if (m) SDL_LockMutex(m); }
static void fire_display_unlockSdl(void) { SDL_mutex* m = fire_display_sdlMutex(); if (m) SDL_UnlockMutex(m); }

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
    for (k = 0; k < 12; k++) if (w->cursors[k]) { SDL_FreeCursor(w->cursors[k]); w->cursors[k] = NULL; }
    w->cursor = -1;
    if (w->texture) { SDL_DestroyTexture(w->texture); w->texture = NULL; }
    for (j = 0; j < w->joystickCount; j++) SDL_JoystickClose(w->joysticks[j]);
    free(w->joysticks);
    w->joysticks = NULL; w->joystickCount = 0; w->joystickCap = 0;
    free(w->dropFiles); free(w->dropText);
    w->dropFiles = w->dropText = NULL; w->dropping = 0;
    if (w->ownsJoystick) { SDL_QuitSubSystem(SDL_INIT_JOYSTICK); w->ownsJoystick = 0; }
    if (w->renderer) { SDL_DestroyRenderer(w->renderer); w->renderer = NULL; }
#if SDL_VERSION_ATLEAST(2, 0, 12)
    if (w->metalView) { SDL_Metal_DestroyView((SDL_MetalView)w->metalView); w->metalView = NULL; }
#endif
    w->direct = 0;
    if (w->window) { SDL_DestroyWindow(w->window); w->window = NULL; }
    if (w->ownsVideo) { SDL_QuitSubSystem(SDL_INIT_VIDEO); w->ownsVideo = 0; }
}

#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT) || defined(FIRE_DISPLAY_SELFTEST_DROP)
/* The tests (and nobody else) push a fixed series of events when a window opens: keys, a click, motion, the wheel, text and the close. */
static void fire_display_selftest(FireDisplayWindow* w) {
    SDL_Event e;
    (void)w;
#ifdef FIRE_DISPLAY_SELFTEST_RESIZE
    /* (only for the test of Window.AutoResize: the window is pulled to 90x70) */
    SDL_zero(e); e.type = SDL_WINDOWEVENT; e.window.event = SDL_WINDOWEVENT_SIZE_CHANGED; e.window.windowID = w->windowId; e.window.data1 = 90; e.window.data2 = 70; SDL_PushEvent(&e);
    return;
#endif
#ifdef FIRE_DISPLAY_SELFTEST_INPUT
    /* (only for the test of touch and joystick: a finger touches, moves and lifts; a joystick moves an axis, presses a button, moves the hat, is plugged in and removed) */
    SDL_zero(e); e.type = SDL_FINGERDOWN; e.tfinger.fingerId = 5; e.tfinger.x = 0.5f; e.tfinger.y = 0.25f; e.tfinger.pressure = 1.0f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_FINGERMOTION; e.tfinger.fingerId = 5; e.tfinger.x = 0.75f; e.tfinger.y = 0.5f; e.tfinger.pressure = 0.5f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_FINGERUP; e.tfinger.fingerId = 5; e.tfinger.x = 0.75f; e.tfinger.y = 0.5f; e.tfinger.pressure = 0.0f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_JOYAXISMOTION; e.jaxis.which = 7; e.jaxis.axis = 1; e.jaxis.value = -32768; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_JOYBUTTONDOWN; e.jbutton.which = 7; e.jbutton.button = 3; e.jbutton.state = SDL_PRESSED; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_JOYBUTTONUP; e.jbutton.which = 7; e.jbutton.button = 3; e.jbutton.state = SDL_RELEASED; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_JOYHATMOTION; e.jhat.which = 7; e.jhat.hat = 0; e.jhat.value = SDL_HAT_UP | SDL_HAT_RIGHT; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_JOYDEVICEREMOVED; e.jdevice.which = 7; SDL_PushEvent(&e);
    return;
#endif
    SDL_zero(e); e.type = SDL_KEYDOWN; e.key.keysym.sym = 'a'; e.key.keysym.scancode = SDL_SCANCODE_A; e.key.keysym.mod = KMOD_LSHIFT; e.key.repeat = 0; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_KEYUP; e.key.keysym.sym = 'a'; e.key.keysym.scancode = SDL_SCANCODE_A; e.key.keysym.mod = 0; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_MOUSEBUTTONDOWN; e.button.button = SDL_BUTTON_LEFT; e.button.x = 32; e.button.y = 24; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_MOUSEBUTTONUP; e.button.button = SDL_BUTTON_LEFT; e.button.x = 32; e.button.y = 24; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_MOUSEMOTION; e.motion.x = 10; e.motion.y = 12; e.motion.xrel = 3; e.motion.yrel = -2; e.motion.state = 1; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_MOUSEWHEEL; e.wheel.preciseX = 0; e.wheel.preciseY = 1.5f; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_TEXTINPUT; strncpy(e.text.text, "\xC3\xA9x", sizeof e.text.text - 1); SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_WINDOWEVENT; e.window.event = SDL_WINDOWEVENT_CLOSE; SDL_PushEvent(&e);
#ifdef FIRE_DISPLAY_SELFTEST_DROP
    /* two files and a text are dropped at (40, 30) of the window; then a single file without begin/complete (an old SDL) */
    SDL_WarpMouseInWindow(w->window, 40, 30);
    SDL_zero(e); e.type = SDL_DROPBEGIN; e.drop.windowID = w->windowId; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_DROPFILE; e.drop.windowID = w->windowId; e.drop.file = SDL_strdup("/tmp/eins.txt"); SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_DROPFILE; e.drop.windowID = w->windowId; e.drop.file = SDL_strdup("/tmp/zwei \xC3\xA4.png"); SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_DROPTEXT; e.drop.windowID = w->windowId; e.drop.file = SDL_strdup("gezogener Text"); SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_DROPCOMPLETE; e.drop.windowID = w->windowId; SDL_PushEvent(&e);
    SDL_zero(e); e.type = SDL_DROPFILE; e.drop.windowID = w->windowId; e.drop.file = SDL_strdup("/tmp/allein.txt"); SDL_PushEvent(&e);
#endif
    SDL_zero(e); e.type = SDL_QUIT; SDL_PushEvent(&e);
}
#endif

static int fire_display_open(FireDisplayWindow* w, const char* title, int width, int height, int vsync) {
    w->vsync = vsync;
    w->winW = width; w->winH = height;
    SDL_SetMainReady();
    /* every window holds a reference of its own (SDL counts them): closing one window must not shut the video down under the others */
    if (SDL_InitSubSystem(SDL_INIT_VIDEO) != 0) return 0;
    w->ownsVideo = 1;
    w->window = SDL_CreateWindow(title, SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, width, height, SDL_WINDOW_RESIZABLE);
    if (!w->window) { fire_display_close(w); return 0; }
    w->renderer = SDL_CreateRenderer(w->window, -1, vsync ? SDL_RENDERER_PRESENTVSYNC : 0);
    if (!w->renderer) w->renderer = SDL_CreateRenderer(w->window, -1, SDL_RENDERER_SOFTWARE);
    if (!w->renderer) { fire_display_close(w); return 0; }
    SDL_StartTextInput();
    w->windowId = SDL_GetWindowID(w->window);
    /* joysticks are an extra: without a driver the window opens all the same (the devices that are plugged in report themselves as SDL_JOYDEVICEADDED)
     * (a reference of its own as well, see above) */
    if (SDL_InitSubSystem(SDL_INIT_JOYSTICK) == 0) w->ownsJoystick = 1;
    SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, w->touchMouse ? "1" : "0");
    /* what the user drops onto the window: files and text */
    SDL_EventState(SDL_DROPFILE, SDL_ENABLE);
#if SDL_VERSION_ATLEAST(2, 0, 5)
    SDL_EventState(SDL_DROPTEXT, SDL_ENABLE);
#endif
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT) || defined(FIRE_DISPLAY_SELFTEST_DROP)
    fire_display_selftest(w);
#endif
    return 1;
}

static void fire_display_setVSync(FireDisplayWindow* w, int v) { w->vsync = v; if (w->renderer) SDL_RenderSetVSync(w->renderer, v ? 1 : 0); }
/* A new size of the window in pixels of the screen (the program asks for it: no resize event comes back). */
static void fire_display_setSize(FireDisplayWindow* w, int width, int height) { if (w->window && width > 0 && height > 0) { SDL_SetWindowSize(w->window, width, height); w->winW = width; w->winH = height; } }
/* The mouse pointer over the window: a system cursor (SDL_SystemCursor: 0 arrow, 1 text, ... 11 hand), made once per kind. */
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

/* Where a drop lands, in framebuffer pixels: SDL 2 does not say - the pointer is asked (on the desktop it is where the user let go). */
static void fire_display_dropPosition(FireDisplayWindow* w, float* x, float* y) {
    int gx = 0, gy = 0, wx = 0, wy = 0, lx, ly;
    SDL_GetGlobalMouseState(&gx, &gy);
    if (w->window) SDL_GetWindowPosition(w->window, &wx, &wy);
    lx = gx - wx;
    ly = gy - wy;
    if ((lx < 0 || ly < 0 || lx >= w->winW || ly >= w->winH) && SDL_GetMouseFocus() == w->window) SDL_GetMouseState(&lx, &ly);
    if (lx < 0) lx = 0;
    if (ly < 0) ly = 0;
    if (lx >= w->winW && w->winW > 0) lx = w->winW - 1;
    if (ly >= w->winH && w->winH > 0) ly = w->winH - 1;
    fire_display_toFramebuffer(w, (float)lx, (float)ly, x, y);
}

/* The drop that was collected goes out: a DropFiles event with the paths, a DropText event with the text. */
static void fire_display_dropDone(FireDisplayWindow* w, FireDisplayEvents* out) {
    FireDisplayEvent e;
    float x, y;
    fire_display_dropPosition(w, &x, &y);
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
    free(w->dropFiles); free(w->dropText);
    w->dropFiles = w->dropText = NULL;
    w->dropping = 0;
}

/* Takes the pending events of this window. 0 once the window was closed. */
static int fire_display_pump(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    SDL_Event ev;
    w->fbWidth = fbWidth; w->fbHeight = fbHeight;
#ifdef __EMSCRIPTEN__
    emscripten_sleep(0);   /* the browser delivers events only while the program is not running */
#endif
    fire_display_lockSdl();
    while (SDL_PollEvent(&ev)) {
        FireDisplayEvent e;
        fire_display_eventInit(&e);
        switch (ev.type) {
            case SDL_QUIT: e.type = FIRE_EV_CLOSE; fire_display_push(out, &e); w->quit = 1; break;
            case SDL_WINDOWEVENT:
                if (ev.window.event == SDL_WINDOWEVENT_CLOSE) { e.type = FIRE_EV_CLOSE_REQUEST; fire_display_push(out, &e); }
                else if (ev.window.event == SDL_WINDOWEVENT_SIZE_CHANGED && ev.window.windowID == w->windowId && (ev.window.data1 != w->winW || ev.window.data2 != w->winH)) {
                    w->winW = ev.window.data1; w->winH = ev.window.data2;
                    e.type = FIRE_EV_RESIZE; e.width = w->winW; e.height = w->winH; fire_display_push(out, &e); }
                break;
            case SDL_KEYDOWN: case SDL_KEYUP:
                e.type = ev.type == SDL_KEYDOWN ? FIRE_EV_KEY_DOWN : FIRE_EV_KEY_UP;
                e.keyCode = (int)ev.key.keysym.sym; e.scanCode = (int)ev.key.keysym.scancode; e.modifier = (int)ev.key.keysym.mod; e.repeat = ev.key.repeat != 0;
                fire_display_push(out, &e);
                break;
            case SDL_MOUSEBUTTONDOWN: case SDL_MOUSEBUTTONUP:
                e.type = ev.type == SDL_MOUSEBUTTONDOWN ? FIRE_EV_MOUSE_DOWN : FIRE_EV_MOUSE_UP;
                e.button = ev.button.button;
                fire_display_toFramebuffer(w, (float)ev.button.x, (float)ev.button.y, &e.x, &e.y);
                fire_display_push(out, &e);
                break;
            case SDL_MOUSEMOTION: {
                FireDisplayEvent r;
                float ox, oy, mx, my;
                e.type = FIRE_EV_MOUSE_MOVE; e.buttonState = (int)ev.motion.state;
                fire_display_toFramebuffer(w, (float)ev.motion.x, (float)ev.motion.y, &e.x, &e.y);
                fire_display_push(out, &e);
                fire_display_eventInit(&r);
                r.type = FIRE_EV_MOUSE_MOVE_REL; r.buttonState = (int)ev.motion.state;
                fire_display_toFramebuffer(w, 0, 0, &ox, &oy); fire_display_toFramebuffer(w, (float)ev.motion.xrel, (float)ev.motion.yrel, &mx, &my);
                r.xrel = mx - ox; r.yrel = my - oy;
                fire_display_push(out, &r);
                break;
            }
            case SDL_MOUSEWHEEL: {
                int mx = 0, my = 0;
                e.type = FIRE_EV_MOUSE_SCROLL; e.scrollX = ev.wheel.preciseX; e.scrollY = ev.wheel.preciseY;
                SDL_GetMouseState(&mx, &my);
                fire_display_toFramebuffer(w, (float)mx, (float)my, &e.x, &e.y);
                fire_display_push(out, &e);
                break;
            }
            case SDL_FINGERDOWN: case SDL_FINGERMOTION: case SDL_FINGERUP:
                /* SDL reports the position normalized (0 to 1 over the window): in framebuffer pixels it is simply x * width */
                e.type = ev.type == SDL_FINGERDOWN ? FIRE_EV_TOUCH_DOWN : ev.type == SDL_FINGERUP ? FIRE_EV_TOUCH_UP : FIRE_EV_TOUCH_MOVE;
                e.finger = (long long)ev.tfinger.fingerId; e.x = ev.tfinger.x * (float)w->fbWidth; e.y = ev.tfinger.y * (float)w->fbHeight; e.pressure = ev.tfinger.pressure;
                fire_display_push(out, &e);
                break;
            case SDL_JOYDEVICEADDED: {
                SDL_Joystick* js = SDL_JoystickOpen(ev.jdevice.which);
                e.type = FIRE_EV_JOY_ADDED;
                if (js) {
                    e.joystick = (int)SDL_JoystickInstanceID(js);
                    if (w->joystickCount == w->joystickCap) {
                        size_t cap = w->joystickCap ? w->joystickCap * 2 : 4;
                        SDL_Joystick** grown = (SDL_Joystick**)realloc(w->joysticks, sizeof(SDL_Joystick*) * cap);
                        if (grown != NULL) { w->joysticks = grown; w->joystickCap = cap; }
                    }
                    if (w->joystickCount < w->joystickCap) w->joysticks[w->joystickCount++] = js; else SDL_JoystickClose(js);
                } else e.joystick = ev.jdevice.which;
                fire_display_push(out, &e);
                break;
            }
            case SDL_JOYDEVICEREMOVED: {
                size_t k;
                for (k = 0; k < w->joystickCount; k++)
                    if ((int)SDL_JoystickInstanceID(w->joysticks[k]) == (int)ev.jdevice.which) {
                        SDL_JoystickClose(w->joysticks[k]);
                        memmove(w->joysticks + k, w->joysticks + k + 1, sizeof(SDL_Joystick*) * (w->joystickCount - k - 1));
                        w->joystickCount--;
                        break;
                    }
                e.type = FIRE_EV_JOY_REMOVED; e.joystick = (int)ev.jdevice.which;
                fire_display_push(out, &e);
                break;
            }
            case SDL_JOYAXISMOTION: {
                float v = (float)ev.jaxis.value / 32767.0f;
                e.type = FIRE_EV_JOY_AXIS; e.joystick = (int)ev.jaxis.which; e.index = ev.jaxis.axis;
                e.value = v < -1.0f ? -1.0f : v;   /* 16 bit, -32768 to 32767 -> -1 to 1 */
                fire_display_push(out, &e);
                break;
            }
            case SDL_JOYBUTTONDOWN: case SDL_JOYBUTTONUP:
                e.type = ev.type == SDL_JOYBUTTONDOWN ? FIRE_EV_JOY_BUTTON_DOWN : FIRE_EV_JOY_BUTTON_UP; e.joystick = (int)ev.jbutton.which; e.index = ev.jbutton.button;
                fire_display_push(out, &e);
                break;
            case SDL_JOYHATMOTION:
                e.type = FIRE_EV_JOY_HAT; e.joystick = (int)ev.jhat.which; e.index = ev.jhat.hat; e.value = (float)ev.jhat.value;
                fire_display_push(out, &e);
                break;
            case SDL_TEXTINPUT: {
                size_t n = strlen(ev.text.text);
                if (n > sizeof e.text - 1) n = sizeof e.text - 1;
                e.type = FIRE_EV_TEXT_INPUT;
                memcpy(e.text, ev.text.text, n);
                e.text[n] = 0;
                fire_display_push(out, &e);
                break;
            }
#if SDL_VERSION_ATLEAST(2, 0, 5)
            case SDL_DROPBEGIN: w->dropping = 1; break;
            case SDL_DROPTEXT:
                if (ev.drop.file) { w->dropText = fire_display_appendLine(w->dropText, ev.drop.file); SDL_free(ev.drop.file); }
                if (!w->dropping) fire_display_dropDone(w, out);
                break;
            case SDL_DROPCOMPLETE: fire_display_dropDone(w, out); break;
#endif
            case SDL_DROPFILE:
                if (ev.drop.file) { w->dropFiles = fire_display_appendLine(w->dropFiles, ev.drop.file); SDL_free(ev.drop.file); }
                if (!w->dropping) fire_display_dropDone(w, out);   /* (an SDL before 2.0.5 has no begin and complete: every file is a drop of its own) */
                break;
            default: break;
        }
    }
    fire_display_unlockSdl();
    return !w->quit;
}

/* ---- the clipboard of the desktop (text) ---- */
/* The video part of SDL must run for the clipboard (on X11 it needs a window of SDL's own): it is started once and stays. */
static int fire_display_clipboardReady(void) {
    static int ready = 0;
    if (!ready) {
        SDL_SetMainReady();
        if (SDL_InitSubSystem(SDL_INIT_VIDEO) != 0) return 0;
        ready = 1;
    }
    /* the events of the clipboard are taken in (X11: another program asks this one for what it put there - a program without a window answers here) */
    SDL_PumpEvents();
    return 1;
}
static int fire_display_clipboardSet(const char* text) {
    int ok;
    fire_display_lockSdl();
    ok = fire_display_clipboardReady() && SDL_SetClipboardText(text) == 0;
    fire_display_unlockSdl();
    return ok;
}
static char* fire_display_clipboardGet(void) {
    char* text = NULL;
    fire_display_lockSdl();
    if (fire_display_clipboardReady() && SDL_HasClipboardText()) {
        char* sdl = SDL_GetClipboardText();
        if (sdl != NULL) {
            size_t n = strlen(sdl);
            text = (char*)malloc(n + 1);
            if (text != NULL) memcpy(text, sdl, n + 1);
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

/* Pictures and files: SDL2 has only text on its clipboard (SDL3 has more, see fire_display_sdl3.h) - not available. */
static unsigned char* fire_display_clipboardGetImage(size_t* size) { *size = 0; return NULL; }
static int fire_display_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)png; (void)pngSize; (void)bmp; (void)bmpSize; return 0; }
static int fire_display_clipboardHasImage(void) { return 0; }
static char* fire_display_clipboardGetFiles(void) { return NULL; }
static int fire_display_clipboardSetFiles(const char* paths) { (void)paths; return 0; }
static int fire_display_clipboardHasFiles(void) { return 0; }

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
    SDL_RenderCopy(w->renderer, w->texture, NULL, NULL);
    SDL_RenderPresent(w->renderer);
#ifdef __EMSCRIPTEN__
    emscripten_sleep(0);   /* in the browser the page draws only when the program hands the thread back */
#endif
}

/* ---- a presenter draws into the window itself (fire_display_c.h: fire_display_native) ----
 * The native handles come from SDL_GetWindowWMInfo. Its header (SDL_syswm.h) would bring the headers of the window system into the program - X11 defines `Bool`, `None`,
 * `Status`, ... - so the structure is mirrored here: it is part of SDL2's stable ABI (the version, the subsystem, a union of 64 bytes at pointer alignment). */
typedef struct FireSdlWmInfo {
    SDL_version version;
    int subsystem;                                   /* SDL_SYSWM_TYPE: 1 Windows, 2 X11, 4 Cocoa, 6 Wayland */
    union {
        struct { void* a; void* b; void* c; } p;     /* Windows: HWND, HDC, HINSTANCE; Wayland: wl_display*, wl_surface* */
        struct { void* display; unsigned long window; } x11;
        Uint8 dummy[64];
    } info;
} FireSdlWmInfo;
extern DECLSPEC SDL_bool SDLCALL SDL_GetWindowWMInfo(SDL_Window* window, FireSdlWmInfo* info);

static int fire_display_native(FireDisplayWindow* w, fire_native_window* out) {
    FireSdlWmInfo info;
    int pw = 0, ph = 0;
    memset(out, 0, sizeof *out);
    if (!w->window) return 0;
    memset(&info, 0, sizeof info);
    SDL_VERSION(&info.version);
    if (!SDL_GetWindowWMInfo(w->window, &info)) return 0;
    switch (info.subsystem) {
        case 1: out->kind = FIRE_NATIVE_WIN32; out->a = info.info.p.c; out->b = info.info.p.a; break;
        case 2: out->kind = FIRE_NATIVE_X11; out->a = info.info.x11.display; out->id = (uint64_t)info.info.x11.window; break;
        case 6: out->kind = FIRE_NATIVE_WAYLAND; out->a = info.info.p.a; out->b = info.info.p.b; break;
#if SDL_VERSION_ATLEAST(2, 0, 12)
        case 4:
            if (!w->metalView) w->metalView = SDL_Metal_CreateView(w->window);
            if (!w->metalView) return 0;
            out->kind = FIRE_NATIVE_METAL; out->b = SDL_Metal_GetLayer((SDL_MetalView)w->metalView);
            break;
#endif
        default: return 0;
    }
    /* the window is the presenter's now: what showed the framebuffers goes (a swap chain of another API on the window would stand in the way) */
    if (!w->direct) {
        if (w->texture) { SDL_DestroyTexture(w->texture); w->texture = NULL; }
        if (w->renderer) { SDL_DestroyRenderer(w->renderer); w->renderer = NULL; }
        w->direct = 1;
    }
#if SDL_VERSION_ATLEAST(2, 26, 0)
    SDL_GetWindowSizeInPixels(w->window, &pw, &ph);
#else
    SDL_GetWindowSize(w->window, &pw, &ph);
#endif
    out->width = pw; out->height = ph;
    out->vsync = w->vsync;
    return 1;
}

static void fire_display_restore(FireDisplayWindow* w) {
    if (!w->direct) return;
    w->direct = 0;
#if SDL_VERSION_ATLEAST(2, 0, 12)
    if (w->metalView) { SDL_Metal_DestroyView((SDL_MetalView)w->metalView); w->metalView = NULL; }
#endif
    if (!w->renderer && w->window) {
        w->renderer = SDL_CreateRenderer(w->window, -1, w->vsync ? SDL_RENDERER_PRESENTVSYNC : 0);
        if (!w->renderer) w->renderer = SDL_CreateRenderer(w->window, -1, SDL_RENDERER_SOFTWARE);
    }
}

#endif
