/* fire native platform package "dos32": DOS with the extender DOS/32A from the 386 on (ROADMAP 13.4) - Open Watcom (wcc386), flat 32-bit memory; the core of the
 * platform is the one of DOS (std/fire_platform_dos.h: the clock, sleep and exit of the C library work under DPMI as in real mode). No fire threads yet. No GPU. */
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
