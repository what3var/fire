// fire native platform package "freertos": the file system for the IO bridge - POSIX calls when the C library has a file system, FIRE_NO_FS for a board without one.
#pragma once
#ifdef FIRE_NO_FS
#include "../std/fire_fs_none.hpp"
#else
#include "../std/fire_fs_posix.hpp"
#endif
