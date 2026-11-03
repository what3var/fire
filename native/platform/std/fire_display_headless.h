/* fire native platform package "std": a display without a screen (fire_dhl_*, one of the displays of fire_display_desktop.h: FIRE_DISPLAY=headless) - for tests, servers
 * and as the simplest complete display (the starting point of a board display).
 * A window is only a size: what is shown stays in memory (fire_dhl_headlessFrame), events come from the tests (FIRE_DISPLAY_SELFTEST*, fire_display_common.h) or from
 * whoever drives the window (fire_dhl_headlessPush). The clipboard is the program's own; nothing can be dragged out. */
#ifndef FIRE_DISPLAY_HEADLESS_H
#define FIRE_DISPLAY_HEADLESS_H

#include "fire_display_common.h"


typedef struct FireDhlWindow {
    int open, vsync, touchMouse, cursor;
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
static void fire_dhl_setCursor(FireDhlWindow* w, int c) { if (c >= 0 && c < FIRE_CURSOR_COUNT) w->cursor = c; }
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

static int fire_dhl_pump(FireDhlWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    if (!w->open) return 0;
    fire_display_queueTake(&w->queue, out, w->winW, w->winH, fbWidth, fbHeight);
    return !w->queue.quit;
}

static void fire_dhl_present(FireDhlWindow* w, const uint32_t* rgba, int width, int height) {
    size_t n;
    if (!w->open || rgba == NULL || width <= 0 || height <= 0) return;
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

static int fire_dhl_startDrag(FireDhlWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
static int fire_dhl_canStartDrag(FireDhlWindow* w) { (void)w; return 0; }

#endif
