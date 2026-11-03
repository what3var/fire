/* fire native platform package "std": a window on Wayland (fire_dwl_*, one of the displays of fire_display_desktop.h). (Not written yet: it says that it cannot open a window.) */
#ifndef FIRE_DISPLAY_WAYLAND_H
#define FIRE_DISPLAY_WAYLAND_H

#include "fire_display_common.h"

typedef struct FireDwlWindow { int unused; } FireDwlWindow;
static int fire_dwl_available(void) { return 0; }
static void fire_dwl_threadDone(void) {}
static void fire_dwl_init(FireDwlWindow* w) { memset(w, 0, sizeof *w); }
static int fire_dwl_open(FireDwlWindow* w, const char* title, int width, int height, int vsync) { (void)w; (void)title; (void)width; (void)height; (void)vsync; return 0; }
static void fire_dwl_close(FireDwlWindow* w) { (void)w; }
static void fire_dwl_setVSync(FireDwlWindow* w, int v) { (void)w; (void)v; }
static void fire_dwl_setSize(FireDwlWindow* w, int width, int height) { (void)w; (void)width; (void)height; }
static void fire_dwl_setCursor(FireDwlWindow* w, int c) { (void)w; (void)c; }
static void fire_dwl_setTouchMouse(FireDwlWindow* w, int v) { (void)w; (void)v; }
static void fire_dwl_setTextInputRect(FireDwlWindow* w, int x, int y, int width, int height) { (void)w; (void)x; (void)y; (void)width; (void)height; }
static int fire_dwl_setBackdrop(FireDwlWindow* w, int kind) { (void)w; (void)kind; return FIRE_BACKDROP_NONE; }
static double fire_dwl_scale(FireDwlWindow* w) { (void)w; return 1.0; }
static int fire_dwl_pump(FireDwlWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) { (void)w; (void)out; (void)fbWidth; (void)fbHeight; return 0; }
static void fire_dwl_present(FireDwlWindow* w, const uint32_t* rgba, int width, int height) { (void)w; (void)rgba; (void)width; (void)height; }
static int fire_dwl_native(FireDwlWindow* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_dwl_restore(FireDwlWindow* w) { (void)w; }
static int fire_dwl_startDrag(FireDwlWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
static int fire_dwl_canStartDrag(FireDwlWindow* w) { (void)w; return 0; }
static int fire_dwl_clipboardSet(const char* text) { return fire_display_memSetText(text); }
static char* fire_dwl_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_dwl_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_dwl_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_dwl_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_dwl_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_dwl_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_dwl_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_dwl_clipboardHasFiles(void) { return fire_display_memHasFiles(); }

#endif
