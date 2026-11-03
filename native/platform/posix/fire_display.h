/* fire native platform package "posix": the window of the windows bridge in C - fire's own display (../std/fire_display_desktop.h: Wayland or X11 on Linux, Cocoa on macOS,
 * without SDL), or SDL when the build asks for it (FIRE_DISPLAY_SDL: the target's "sdl" or FIRE_SDL) - SDL3 with FIRE_SDL3 (../std/fire_display_sdl3.h), else SDL2
 * (../std/fire_display_sdl.h). */
#ifndef FIRE_PLATFORM_DISPLAY_H
#define FIRE_PLATFORM_DISPLAY_H
#if defined(FIRE_DISPLAY_SDL) && defined(FIRE_SDL3)
#include "../std/fire_display_sdl3.h"
#elif defined(FIRE_DISPLAY_SDL)
#include "../std/fire_display_sdl.h"
#else
#include "../std/fire_display_desktop.h"
#endif
#endif
