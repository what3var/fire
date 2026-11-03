/* fire native platform package "posix": the window of the windows bridge in C: SDL3 when the build defines FIRE_SDL3
 * (the generated program says `// fire-link: SDL3`, see ../std/fire_display_sdl3.h), else SDL2 (../std/fire_display_sdl.h). */
#ifndef FIRE_PLATFORM_DISPLAY_H
#define FIRE_PLATFORM_DISPLAY_H
#ifdef FIRE_SDL3
#include "../std/fire_display_sdl3.h"
#else
#include "../std/fire_display_sdl.h"
#endif
#endif
