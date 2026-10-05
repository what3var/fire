// fire native platform package "esp32": the file system for the IO bridge on the ESP-IDF virtual file system (POSIX calls; mount SPIFFS/FAT/LittleFS yourself and set FIRE_FS_ROOT).
#pragma once
#ifdef FIRE_NO_FS
#include "../std/fire_fs_none.hpp"
#else
#include "../std/fire_fs_posix.hpp"
#endif
