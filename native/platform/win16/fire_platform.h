/* fire native platform package "win16": Windows 3.x (ROADMAP 13.7) - Open Watcom (wcc, -bwindows), the huge model of the 16-bit protected mode; the core is
 * std/fire_platform_win16.h (GetTickCount, sleep with the message loop, WinMain, the output in a file or a window). fire threads over fire's own scheduler, no GPU. */
#ifndef FIRE_PLATFORM_H
#define FIRE_PLATFORM_H
#ifndef FIRE_GFX_NO_GPU
#define FIRE_GFX_NO_GPU 1
#endif
#ifndef FIRE_WINDOW_NO_THREADS
#define FIRE_WINDOW_NO_THREADS 1
#endif
#include "../std/fire_platform_win16.h"
#endif
