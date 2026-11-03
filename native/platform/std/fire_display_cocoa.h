/* fire native platform package "std": a window on Cocoa (fire_dns_*, one of the displays of fire_display_desktop.h). (Not written yet: it says that it cannot open a window.) */
#ifndef FIRE_DISPLAY_COCOA_H
#define FIRE_DISPLAY_COCOA_H

#include "fire_display_common.h"

typedef struct FireDnsWindow { int unused; } FireDnsWindow;
static int fire_dns_available(void) { return 0; }
static void fire_dns_threadDone(void) {}
static void fire_dns_init(FireDnsWindow* w) { memset(w, 0, sizeof *w); }
static int fire_dns_open(FireDnsWindow* w, const char* title, int width, int height, int vsync) { (void)w; (void)title; (void)width; (void)height; (void)vsync; return 0; }
static void fire_dns_close(FireDnsWindow* w) { (void)w; }
static void fire_dns_setVSync(FireDnsWindow* w, int v) { (void)w; (void)v; }
static void fire_dns_setSize(FireDnsWindow* w, int width, int height) { (void)w; (void)width; (void)height; }
static void fire_dns_setCursor(FireDnsWindow* w, int c) { (void)w; (void)c; }
static void fire_dns_setTouchMouse(FireDnsWindow* w, int v) { (void)w; (void)v; }
static void fire_dns_setTextInputRect(FireDnsWindow* w, int x, int y, int width, int height) { (void)w; (void)x; (void)y; (void)width; (void)height; }
static int fire_dns_setBackdrop(FireDnsWindow* w, int kind) { (void)w; (void)kind; return FIRE_BACKDROP_NONE; }
static double fire_dns_scale(FireDnsWindow* w) { (void)w; return 1.0; }
static int fire_dns_pump(FireDnsWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) { (void)w; (void)out; (void)fbWidth; (void)fbHeight; return 0; }
static void fire_dns_present(FireDnsWindow* w, const uint32_t* rgba, int width, int height) { (void)w; (void)rgba; (void)width; (void)height; }
static int fire_dns_native(FireDnsWindow* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_dns_restore(FireDnsWindow* w) { (void)w; }
static int fire_dns_startDrag(FireDnsWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
static int fire_dns_canStartDrag(FireDnsWindow* w) { (void)w; return 0; }
static int fire_dns_clipboardSet(const char* text) { return fire_display_memSetText(text); }
static char* fire_dns_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_dns_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_dns_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_dns_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_dns_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_dns_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_dns_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_dns_clipboardHasFiles(void) { return fire_display_memHasFiles(); }

#endif
