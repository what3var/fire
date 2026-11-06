/* fire native platform package "dos16": DOS in real mode from the 8086 on (ROADMAP 13.3) - Open Watcom (wcc), the huge model; the core of the platform is the
 * one of DOS (std/fire_platform_dos.h). fire threads are tasks of fire's own scheduler. No GPU: the graphics library draws in software only. */
#ifndef FIRE_PLATFORM_H
#define FIRE_PLATFORM_H
#ifndef FIRE_GFX_NO_GPU
#define FIRE_GFX_NO_GPU 1
#endif
#include "../std/fire_platform_dos.h"
#endif
