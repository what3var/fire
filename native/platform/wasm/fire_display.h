/* The window of a program in the browser: fire's own display (../std/fire_display_desktop.h -> fire_display_web.h: a canvas of the page, without SDL), or SDL2 of
 * Emscripten (-sUSE_SDL=2, ../std/fire_display_sdl.h) when the build asks for it (FIRE_DISPLAY_SDL). */
#ifndef FIRE_PLATFORM_DISPLAY_H
#define FIRE_PLATFORM_DISPLAY_H
#ifdef FIRE_DISPLAY_SDL
#include "../std/fire_display_sdl.h"
#else
#include "../std/fire_display_desktop.h"
#endif
#endif
