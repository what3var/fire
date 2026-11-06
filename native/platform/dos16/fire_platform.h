/* fire native platform package "dos16": DOS in real mode from the 8086 on (ROADMAP 13.3) - Open Watcom (wcc), the huge model; the core of the platform is the
 * one of DOS (std/fire_platform_dos.h). fire threads are tasks of fire's own scheduler. No GPU: the graphics library draws in software only; windows are the screen in a mode of the VGA (std/fire_display_dos.h). */
#ifndef FIRE_PLATFORM_H
#define FIRE_PLATFORM_H
#ifndef FIRE_GFX_NO_GPU
#define FIRE_GFX_NO_GPU 1
#endif
#ifndef FIRE_WINDOW_NO_THREADS
#define FIRE_WINDOW_NO_THREADS 1   /* (the program shows its windows itself: no window manager's thread) */
#endif
#include "../std/fire_platform_dos.h"
#endif
