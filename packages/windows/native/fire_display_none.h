/* fire native platform package "std": no display (a board without a screen, or one whose package you write yourself). A window cannot be created - `new Window(...)` throws a
 * HandleUnavailableException. A board package of your own provides its own fire_display.h (see fire_display_c.h for the functions and what they do) and puts
 * the pixels on its screen. */
#ifndef FIRE_DISPLAY_NONE_H
#define FIRE_DISPLAY_NONE_H

#include "fire_display_c.h"

#define FIRE_DISPLAY_AVAILABLE 0

/* A board: no desktop to ask - the UI uses its embedded look (the fast built-in font, whole-number scaling, dark violet). */
static void fire_display_systemTheme(FireSystemTheme* t) {
    fire_display_themeDefaults(t);
    t->embedded = 1;
    t->transparency = 0;   /* (a board: no glass, no transparency - flat and fast) */
    t->scale = 1.0;
    t->dark = 1;
    t->accent = 0x8854D0;
}

static void fire_display_threadDone(void) {}

typedef struct FireDisplayWindow { int vsync, touchMouse; } FireDisplayWindow;

static void fire_display_init(FireDisplayWindow* w) { w->vsync = 1; w->touchMouse = 1; }
static int fire_display_open(FireDisplayWindow* w, const char* title, int width, int height, int vsync) { (void)w; (void)title; (void)width; (void)height; (void)vsync; return 0; }
static void fire_display_setVSync(FireDisplayWindow* w, int v) { w->vsync = v; }
static void fire_display_setSize(FireDisplayWindow* w, int width, int height) { (void)w; (void)width; (void)height; }
static void fire_display_setCursor(FireDisplayWindow* w, int c) { (void)w; (void)c; }
static int fire_display_vsync(const FireDisplayWindow* w) { return w->vsync; }
static void fire_display_setTouchMouse(FireDisplayWindow* w, int v) { w->touchMouse = v; }
static int fire_display_touchMouse(const FireDisplayWindow* w) { return w->touchMouse; }
static int fire_display_pump(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) { (void)w; (void)out; (void)fbWidth; (void)fbHeight; return 0; }
static void fire_display_present(FireDisplayWindow* w, const uint32_t* rgba, int width, int height) { (void)w; (void)rgba; (void)width; (void)height; }
static void fire_display_close(FireDisplayWindow* w) { (void)w; }
static int fire_display_native(FireDisplayWindow* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_display_restore(FireDisplayWindow* w) { (void)w; }

/* No desktop: the clipboard is the program's own (Clipboard.SetText/GetText, SetImage/GetImage, SetFiles/GetFiles work within it). */
static int fire_display_clipboardSet(const char* text) { return fire_display_memSetText(text); }
static char* fire_display_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_display_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_display_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_display_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_display_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_display_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_display_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_display_clipboardHasFiles(void) { return fire_display_memHasFiles(); }

/* No desktop: nothing to drag into (Window.StartDrag says 0). */
#define FIRE_DISPLAY_START_DRAG 1
static int fire_display_startDrag(FireDisplayWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
#define FIRE_DISPLAY_CAN_START_DRAG 1
static int fire_display_canStartDrag(FireDisplayWindow* w) { (void)w; return 0; }

#endif
