/* fire native platform package "std": a display without a screen (fire_dhl_*, one of the displays of fire_display_desktop.h: FIRE_DISPLAY=headless) - for tests, servers
 * and as the simplest complete display (the starting point of a board display).
 * A window is only a size: what is shown stays in memory (fire_dhl_headlessFrame), events come from the tests (FIRE_DISPLAY_SELFTEST*, fire_display_common.h) or from
 * whoever drives the window (fire_dhl_headlessPush). The clipboard is the program's own; nothing can be dragged out.
 *
 * A HOST can drive it (fire_display_host): the editor's live view of a program and its design view (the library of the VM, native/abi/fire_gfx_host.h). Then every window
 * of the program is one of this display, whatever FIRE_DISPLAY says; its events come from the host (next_event, asked when the program pumps), its pictures, its mouse
 * pointer and its drags go to the host (present, cursor, start_drag; each may be NULL). */
#ifndef FIRE_DISPLAY_HEADLESS_H
#define FIRE_DISPLAY_HEADLESS_H

#include "fire_display_common.h"


/* An event from the host: `type` as FIRE_EV_*, positions in pixels of the window. MOUSE_DOWN/UP: button, x, y; MOUSE_MOVE: x, y; MOUSE_SCROLL: dx, dy (notches, y > 0: up),
 * x, y; KEY_DOWN/UP: keyCode, modifiers (fire_display_keys.h); TEXT_INPUT: text (UTF-8); RESIZE: width, height; TOUCH_*: finger, x, y, pressure; JOY_*: joystick, index,
 * value; DROP_FILES (data: the paths, one per line) / DROP_TEXT (data), x, y; DRAG_HOVER: x, y; DRAG_LEAVE, CLOSE, focus and the mouse coming and going. `data` (UTF-8,
 * zero terminated, may be NULL) is the host's: it is copied at once. */
typedef struct FireDisplayHostEvent {
    int32_t type, button, buttons, keyCode, modifiers, width, height;
    float x, y, dx, dy;
    char text[32];
    int32_t joystick, index;
    float value, pressure;
    int32_t reserved;
    int64_t finger;
    const char* data;
} FireDisplayHostEvent;
typedef struct FireDisplayHost {
    int32_t size;                                                                                         /* sizeof(FireDisplayHost) */
    void* ctx;
    int32_t (*next_event)(void* ctx, int32_t window, FireDisplayHostEvent* out);                        /* 1: `out` is the next event of window `window`; 0: none */
    void (*present)(void* ctx, int32_t window, const uint32_t* rgba, int32_t width, int32_t height, int32_t vsync);   /* a picture (on the window's thread) */
    void (*cursor)(void* ctx, int32_t window, int32_t cursor);                                          /* the mouse pointer (FIRE_CURSOR_*) */
    int32_t (*start_drag)(void* ctx, int32_t window, const char* text, const char* files);              /* Window.StartDrag (UTF-8; files one per line): 0 nothing, 1 copied, 2 moved */
    int32_t (*can_start_drag)(void* ctx, int32_t window);                                               /* Window.CanStartDrag: 1 the host takes drags */
} FireDisplayHost;
static FireDisplayHost g_fireDisplayHost;
static volatile int g_fireDisplayHosted;
static int g_fireDhlSerial;

/* The host takes the display over (before the program opens a window; `host` NULL: a display without a screen, no events, nothing goes anywhere). */
static void fire_display_host(const FireDisplayHost* host) {
    memset(&g_fireDisplayHost, 0, sizeof g_fireDisplayHost);
    if (host != NULL) memcpy(&g_fireDisplayHost, host, (size_t)host->size < sizeof g_fireDisplayHost ? (size_t)host->size : sizeof g_fireDisplayHost);
    g_fireDisplayHosted = 1;
    g_fireDhlSerial = 0;   /* (the windows of the next program count from 1) */
}

typedef struct FireDhlWindow {
    int open, vsync, touchMouse, cursor;
    int id;                       /* 1, 2, ...: the windows in the order they opened (what the host is told) */
    int winW, winH;
    FireDisplayQueue queue;
    uint32_t* frame;              /* the pixels shown last (R,G,B,A per 32-bit word) */
    int frameW, frameH;
    long frames;                  /* how many frames were shown */
    int backdrop;
    int textX, textY, textW, textH;   /* where the program's text cursor is */
} FireDhlWindow;

static void fire_dhl_threadDone(void) {}

static void fire_dhl_init(FireDhlWindow* w) {
    memset(w, 0, sizeof *w);
    w->vsync = 1;
    w->touchMouse = 1;
}

static int fire_dhl_open(FireDhlWindow* w, const char* title, int width, int height, int vsync) {
    (void)title;
    if (width <= 0 || height <= 0) return 0;
    fire_display_queueInit(&w->queue);
    w->open = 1; w->vsync = vsync; w->winW = width; w->winH = height;
    w->id = ++g_fireDhlSerial;
#ifdef FIRE_DISPLAY_HAS_SELFTEST
    fire_display_selftest(&w->queue, width, height);
#endif
    return 1;
}

static void fire_dhl_close(FireDhlWindow* w) {
    fire_display_queueFree(&w->queue);
    free(w->frame);
    w->frame = NULL; w->frameW = w->frameH = 0;
    w->open = 0;
}

static void fire_dhl_setVSync(FireDhlWindow* w, int v) { w->vsync = v; }
static int fire_dhl_vsync(const FireDhlWindow* w) { return w->vsync; }
/* (the program asks for the size: no resize event comes back, as on the desktop) */
static void fire_dhl_setSize(FireDhlWindow* w, int width, int height) { if (width > 0 && height > 0) { w->winW = width; w->winH = height; } }
static void fire_dhl_setCursor(FireDhlWindow* w, int c) {
    if (c < 0 || c >= FIRE_CURSOR_COUNT || c == w->cursor) return;
    w->cursor = c;
    if (g_fireDisplayHosted && g_fireDisplayHost.cursor != NULL) g_fireDisplayHost.cursor(g_fireDisplayHost.ctx, w->id, c);
}
static void fire_dhl_setTouchMouse(FireDhlWindow* w, int v) { w->touchMouse = v; }
static int fire_dhl_touchMouse(const FireDhlWindow* w) { return w->touchMouse; }

static void fire_dhl_setTextInputRect(FireDhlWindow* w, int x, int y, int width, int height) { w->textX = x; w->textY = y; w->textW = width; w->textH = height; }

/* Without a desktop nothing lies behind the window: it can only be transparent (the frame keeps its alpha). */
static int fire_dhl_setBackdrop(FireDhlWindow* w, int kind) { w->backdrop = kind <= FIRE_BACKDROP_NONE ? FIRE_BACKDROP_NONE : FIRE_BACKDROP_TRANSPARENT; return w->backdrop; }

static double fire_dhl_scale(FireDhlWindow* w) { (void)w; return 1.0; }

/* An event for the window, as if the desktop had sent it (positions in pixels of the window). */
static void fire_dhl_headlessPush(FireDhlWindow* w, const FireDisplayEvent* e) {
    FireDisplayEvent copy = *e;
    if (e->data != NULL) copy.data = (char*)fire_display_copyBytes(e->data, strlen(e->data));
    if (e->type == FIRE_EV_RESIZE) { w->winW = e->width; w->winH = e->height; }
    fire_display_queuePush(&w->queue, &copy);
}

/* What the host has for the window. */
static void fire_dhl_hostEvents(FireDhlWindow* w) {
    FireDisplayHostEvent h;
    int n;
    for (n = 0; n < 4096; n++) {
        memset(&h, 0, sizeof h);
        if (!g_fireDisplayHost.next_event(g_fireDisplayHost.ctx, w->id, &h)) break;
        switch (h.type) {
            case FIRE_EV_MOUSE_DOWN: case FIRE_EV_MOUSE_UP: fire_display_queueButton(&w->queue, h.type == FIRE_EV_MOUSE_DOWN, h.button, h.x, h.y); break;
            case FIRE_EV_MOUSE_MOVE: fire_display_queueMotion(&w->queue, h.x, h.y); break;
            case FIRE_EV_MOUSE_SCROLL: fire_display_queueScroll(&w->queue, h.dx, h.dy, h.x, h.y); break;
            case FIRE_EV_KEY_DOWN: case FIRE_EV_KEY_UP: fire_display_queueKey(&w->queue, h.type == FIRE_EV_KEY_DOWN, h.keyCode, 0, h.modifiers, 0); break;
            case FIRE_EV_TEXT_INPUT: h.text[sizeof h.text - 1] = 0; fire_display_queueText(&w->queue, h.text, strlen(h.text)); break;
            case FIRE_EV_RESIZE: if (h.width > 0 && h.height > 0) { w->winW = h.width; w->winH = h.height; fire_display_queueResize(&w->queue, h.width, h.height); } break;
            case FIRE_EV_TOUCH_DOWN: case FIRE_EV_TOUCH_MOVE: case FIRE_EV_TOUCH_UP: fire_display_queueTouch(&w->queue, h.type, (long long)h.finger, h.x, h.y, h.pressure); break;
            case FIRE_EV_JOY_AXIS: case FIRE_EV_JOY_BUTTON_DOWN: case FIRE_EV_JOY_BUTTON_UP: case FIRE_EV_JOY_HAT: case FIRE_EV_JOY_ADDED: case FIRE_EV_JOY_REMOVED:
                fire_display_queueJoy(&w->queue, h.type, h.joystick, h.index, h.value); break;
            case FIRE_EV_DROP_FILES: case FIRE_EV_DROP_TEXT: case FIRE_EV_DRAG_HOVER: {
                FireDisplayEvent e;
                fire_display_eventInit(&e);
                e.type = h.type; e.x = h.x; e.y = h.y;
                if (h.data != NULL && h.type != FIRE_EV_DRAG_HOVER) e.data = (char*)fire_display_copyBytes(h.data, strlen(h.data));
                fire_display_queuePush(&w->queue, &e);
                break;
            }
            case FIRE_EV_CLOSE: case FIRE_EV_CLOSE_REQUEST: case FIRE_EV_MOUSE_ENTER: case FIRE_EV_MOUSE_LEAVE: case FIRE_EV_FOCUS_GAINED: case FIRE_EV_FOCUS_LOST: case FIRE_EV_DRAG_LEAVE:
                fire_display_queueSimple(&w->queue, h.type); break;
            default: break;
        }
    }
}

static int fire_dhl_pump(FireDhlWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    if (!w->open) return 0;
    if (g_fireDisplayHosted && g_fireDisplayHost.next_event != NULL && !w->queue.quit) fire_dhl_hostEvents(w);
    fire_display_queueTake(&w->queue, out, w->winW, w->winH, fbWidth, fbHeight);
    return !w->queue.quit;
}

static void fire_dhl_present(FireDhlWindow* w, const uint32_t* rgba, int width, int height) {
    size_t n;
    if (!w->open || rgba == NULL || width <= 0 || height <= 0) return;
    if (g_fireDisplayHosted) {
        /* (the host keeps the picture itself) */
        w->frames++;
        if (g_fireDisplayHost.present != NULL) g_fireDisplayHost.present(g_fireDisplayHost.ctx, w->id, rgba, width, height, w->vsync);
        return;
    }
    n = (size_t)width * (size_t)height;
    if (w->frame == NULL || w->frameW != width || w->frameH != height) {
        uint32_t* grown = (uint32_t*)realloc(w->frame, n * sizeof(uint32_t));
        if (grown == NULL) return;
        w->frame = grown; w->frameW = width; w->frameH = height;
    }
    memcpy(w->frame, rgba, n * sizeof(uint32_t));
    w->frames++;
}

/* The pixels shown last (NULL before the first frame). */
static const uint32_t* fire_dhl_headlessFrame(const FireDhlWindow* w, int* width, int* height) { *width = w->frameW; *height = w->frameH; return w->frame; }

static int fire_dhl_native(FireDhlWindow* w, fire_native_window* out) { (void)w; memset(out, 0, sizeof *out); return 0; }
static void fire_dhl_restore(FireDhlWindow* w) { (void)w; }

/* The program's own clipboard (Clipboard.SetText/GetText, SetImage/GetImage, SetFiles/GetFiles work within it). */
static int fire_dhl_clipboardSet(const char* text) { return fire_display_memSetText(text); }
static char* fire_dhl_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_dhl_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_dhl_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_dhl_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_dhl_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_dhl_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_dhl_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_dhl_clipboardHasFiles(void) { return fire_display_memHasFiles(); }

static int fire_dhl_startDrag(FireDhlWindow* w, const char* text, const char* files) {
    if (g_fireDisplayHosted && g_fireDisplayHost.start_drag != NULL) return g_fireDisplayHost.start_drag(g_fireDisplayHost.ctx, w->id, text ? text : "", files ? files : "");
    return 0;
}
static int fire_dhl_canStartDrag(FireDhlWindow* w) { return g_fireDisplayHosted && g_fireDisplayHost.can_start_drag != NULL && g_fireDisplayHost.can_start_drag(g_fireDisplayHost.ctx, w->id) != 0; }

#endif
