/* fire io package: the selector of the boards (esp32, freertos): the file system for the io bridge in C - POSIX calls when the C library has a file system (ESP-IDF: mount SPIFFS/FAT/LittleFS yourself
 * and set FIRE_FS_ROOT), FIRE_NO_FS for a board without one. */
#ifndef FIRE_FS_BOARD_H
#define FIRE_FS_BOARD_H
#ifdef FIRE_NO_FS
#include "fire_fs_none.h"
#else
#ifndef FIRE_FS_ROOT
#define FIRE_FS_ROOT "/"
#endif
#ifndef FIRE_FS_SMALL_OFFSETS
#define FIRE_FS_SMALL_OFFSETS 1   /* (newlib of the boards: ftell/fseek) */
#endif
#include "fire_fs_posix.h"
#endif
#endif
