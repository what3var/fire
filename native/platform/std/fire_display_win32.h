/* fire native platform package "std": a window on Win32 (fire_dw32_*, one of the displays of fire_display_desktop.h). (Not written yet: it says that it cannot open a window.) */
#ifndef FIRE_DISPLAY_WIN32_H
#define FIRE_DISPLAY_WIN32_H

#include "fire_display_common.h"

typedef struct FireDw32Window { int unused; } FireDw32Window;
static int fire_dw32_available(void) { return 0; }
static void fire_dw32_threadDone(void) {}
static void fire_dw32_init(FireDw32Window* w) { memset(w, 0, sizeof *w); }
static int fire_dw32_open(FireDw32Window* w, const char* title, int width, int height, int vsync) { (void)w; (void)title; (void)width; (void)height; (void)vsync; return 0; }
static void fire_dw32_close(FireDw32Window* w) { (void)w; }
static void fire_dw32_setVSync(FireDw32Window* w, int v) { (void)w; (void)v; }
static void fire_dw32_setSize(FireDw32Window* w, int width, int height) { (void)w; (void)width; (void)height; }
static void fire_dw32_setCursor(FireDw32Window* w, int c) { (void)w; (void)c; }
static void fire_dw32_setTouchMouse(FireDw32Window* w, int v) { (void)w; (void)v; }
static void fire_dw32_setTextInputRect(FireDw32Window* w, int x, int y, int width, int height) { (void)w; (void)x; (void)y; (void)width; (void)height; }
static int fire_dw32_setBackdrop(FireDw32Window* w, int kind) { (void)w; (void)kind; return FIRE_BACKDROP_NONE; }
static double fire_dw32_scale(FireDw32Window* w) { (void)w; return 1.0; }
static int fire_dw32_pump(FireDw32Window* w, FireDisplayEvents* out, int fbWidth, int fbHeight) { (void)w; (void)out; (void)fbWidth; (void)fbHeight; return 0; }
static void fire_dw32_present(FireDw32Window* w, const uint32_t* rgba, int width, int height) { (void)w; (void)rgba; (void)width; (void)height; }
static int fire_dw32_native(FireDw32Window* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_dw32_restore(FireDw32Window* w) { (void)w; }
static int fire_dw32_startDrag(FireDw32Window* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
static int fire_dw32_canStartDrag(FireDw32Window* w) { (void)w; return 0; }
static int fire_dw32_clipboardSet(const char* text) { return fire_display_memSetText(text); }
static char* fire_dw32_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_dw32_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_dw32_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_dw32_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_dw32_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_dw32_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_dw32_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_dw32_clipboardHasFiles(void) { return fire_display_memHasFiles(); }

#endif
