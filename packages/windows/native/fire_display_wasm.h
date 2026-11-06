/* The window of a program in the browser: fire's own display (fire_display_desktop.h -> fire_display_web.h: a canvas of the page, without SDL), or SDL2 of
 * Emscripten (-sUSE_SDL=2, fire_display_sdl.h) when the build asks for it (FIRE_DISPLAY_SDL). */
#ifndef FIRE_DISPLAY_WASM_H
#define FIRE_DISPLAY_WASM_H
#ifdef FIRE_DISPLAY_SDL
#include "fire_display_sdl.h"
#else
#include "fire_display_desktop.h"
#endif
#endif
