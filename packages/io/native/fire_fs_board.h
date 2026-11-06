/* fire io package: the file system of a board (esp32, FreeRTOS): the POSIX calls of the C library when it has a file system (ESP-IDF: mount SPIFFS/FAT/LittleFS
 * yourself and set FIRE_FS_ROOT) - fire_fs_posix.h; FIRE_NO_FS in the target's `defines` for a board without one - fire_fs_none.h. */
#ifndef FIRE_FS_BOARD_H
#define FIRE_FS_BOARD_H
#ifndef FIRE_NO_FS
#ifndef FIRE_FS_ROOT
#define FIRE_FS_ROOT "/"
#endif
#ifndef FIRE_FS_SMALL_OFFSETS
#define FIRE_FS_SMALL_OFFSETS 1   /* (newlib of the boards: ftell/fseek) */
#endif
#endif
#endif
