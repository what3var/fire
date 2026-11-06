/* fire windows package: the selector of "posix" and "windows": the window of the windows bridge in C - fire's own display (fire_display_desktop.h: Wayland or X11 on Linux, Cocoa on macOS, Win32 on Windows,
 * without SDL), or SDL when the build asks for it (FIRE_DISPLAY_SDL: the target's "sdl" or FIRE_SDL) - SDL3 with FIRE_SDL3 (fire_display_sdl3.h), else SDL2
 * (fire_display_sdl.h). */
#ifndef FIRE_DISPLAY_SELECT_H
#define FIRE_DISPLAY_SELECT_H
#if defined(FIRE_DISPLAY_SDL) && defined(FIRE_SDL3)
#include "fire_display_sdl3.h"
#elif defined(FIRE_DISPLAY_SDL)
#include "fire_display_sdl.h"
#else
#include "fire_display_desktop.h"
#endif
#endif
