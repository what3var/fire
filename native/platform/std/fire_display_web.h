/* fire native platform package "std": a window on the web (fire_dweb_*, one of the displays of fire_display_desktop.h). (Not written yet: it says that it cannot open a window.) */
#ifndef FIRE_DISPLAY_WEB_H
#define FIRE_DISPLAY_WEB_H

#include "fire_display_common.h"

typedef struct FireDwebWindow { int unused; } FireDwebWindow;
static int fire_dweb_available(void) { return 0; }
static void fire_dweb_threadDone(void) {}
static void fire_dweb_init(FireDwebWindow* w) { memset(w, 0, sizeof *w); }
static int fire_dweb_open(FireDwebWindow* w, const char* title, int width, int height, int vsync) { (void)w; (void)title; (void)width; (void)height; (void)vsync; return 0; }
static void fire_dweb_close(FireDwebWindow* w) { (void)w; }
static void fire_dweb_setVSync(FireDwebWindow* w, int v) { (void)w; (void)v; }
static void fire_dweb_setSize(FireDwebWindow* w, int width, int height) { (void)w; (void)width; (void)height; }
static void fire_dweb_setCursor(FireDwebWindow* w, int c) { (void)w; (void)c; }
static void fire_dweb_setTouchMouse(FireDwebWindow* w, int v) { (void)w; (void)v; }
static void fire_dweb_setTextInputRect(FireDwebWindow* w, int x, int y, int width, int height) { (void)w; (void)x; (void)y; (void)width; (void)height; }
static int fire_dweb_setBackdrop(FireDwebWindow* w, int kind) { (void)w; (void)kind; return FIRE_BACKDROP_NONE; }
static double fire_dweb_scale(FireDwebWindow* w) { (void)w; return 1.0; }
static int fire_dweb_pump(FireDwebWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) { (void)w; (void)out; (void)fbWidth; (void)fbHeight; return 0; }
static void fire_dweb_present(FireDwebWindow* w, const uint32_t* rgba, int width, int height) { (void)w; (void)rgba; (void)width; (void)height; }
static int fire_dweb_native(FireDwebWindow* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_dweb_restore(FireDwebWindow* w) { (void)w; }
static int fire_dweb_startDrag(FireDwebWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
static int fire_dweb_canStartDrag(FireDwebWindow* w) { (void)w; return 0; }
static int fire_dweb_clipboardSet(const char* text) { return fire_display_memSetText(text); }
static char* fire_dweb_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_dweb_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_dweb_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_dweb_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_dweb_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_dweb_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_dweb_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_dweb_clipboardHasFiles(void) { return fire_display_memHasFiles(); }

#endif
