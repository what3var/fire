/* fire native platform package "bios16" (ROADMAP 13.5): a program without an operating system, started by the BIOS, in real mode from the 8086 on - Open Watcom
 * (wcc), the huge model and the widths of dos16. The C library of Open Watcom stays; what it would ask DOS for (the console, the heap, the time, int 21h) is the
 * program's own (fire_bios16_libc.h, the start in fire_bios16_start.asm, the boot sector in fire_bios16_boot.asm). The core of the platform - the clock, sleep, the
 * end - is the one of DOS (std/fire_platform_dos.h): its calls of the C library come here. No files, no network, no sound. */
#ifndef FIRE_PLATFORM_H
#define FIRE_PLATFORM_H
#ifndef FIRE_GFX_NO_GPU
#define FIRE_GFX_NO_GPU 1
#endif
#ifndef FIRE_S3D_LEAN_ONLY
#define FIRE_S3D_LEAN_ONLY 1       /* scene3d draws with its lean renderer only (RenderMode.Fast for every scene: small, bands in the conventional memory) */
#endif
#ifndef FIRE_WINDOW_NO_THREADS
#define FIRE_WINDOW_NO_THREADS 1
#endif
#define FIRE_PLAT_NAME "bios16"
#ifdef FIRE_RT_IMPLEMENTATION
#include "fire_bios16_libc.h"   /* (the program's translation unit only) */
#endif
#include "../std/fire_platform_dos.h"
#endif
