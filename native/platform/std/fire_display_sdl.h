/* fire native platform package "std": a window on the desktop through SDL2 (link with -lSDL2; the generated program asks for it with a `// fire-link: SDL2` line).
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
} FireDisplayWindow;

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
    if (w->ownsJoystick) { SDL_QuitSubSystem(SDL_INIT_JOYSTICK); w->ownsJoystick = 0; }
    if (w->renderer) { SDL_DestroyRenderer(w->renderer); w->renderer = NULL; }
    if (w->window) { SDL_DestroyWindow(w->window); w->window = NULL; }
    if (w->ownsVideo) { SDL_QuitSubSystem(SDL_INIT_VIDEO); w->ownsVideo = 0; }
}

#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT)
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
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT)
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

/* Takes the pending events of this window. 0 once the window was closed. */
static int fire_display_pump(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    SDL_Event ev;
    w->fbWidth = fbWidth; w->fbHeight = fbHeight;
#ifdef __EMSCRIPTEN__
    emscripten_sleep(0);   /* the browser delivers events only while the program is not running */
#endif
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
            default: break;
        }
    }
    return !w->quit;
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
    SDL_RenderCopy(w->renderer, w->texture, NULL, NULL);
    SDL_RenderPresent(w->renderer);
#ifdef __EMSCRIPTEN__
    emscripten_sleep(0);   /* in the browser the page draws only when the program hands the thread back */
#endif
}

#endif
