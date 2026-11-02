/* fire native platform layer, file system part in C for a board without a file system: every file operation fails with "not supported" (the console - IO.Stdio -
 * and memory streams still work). Select it with FIRE_NO_FS in the target configuration (`defines`). The interface: fire_fs_c.h. */
#ifndef FIRE_FS_NONE_H
#define FIRE_FS_NONE_H

#include "fire_fs_c.h"

static int fire_fs_none(FireFsStatus* st) { return fire_fs_fail(st, FIRE_FS_E_UNSUPPORTED, "This platform has no file system.%s", ""); }

static int fire_fs_exists(const char* path, int* isDir) { (void)path; (void)isDir; return 0; }
static FILE* fire_fs_open(const char* path, int mode, int access, FireFsStatus* st) { (void)path; (void)mode; (void)access; fire_fs_none(st); return NULL; }
static int64_t fire_fs_tell(FILE* f) { (void)f; return -1; }
static int fire_fs_seek(FILE* f, int64_t offset, int whence) { (void)f; (void)offset; (void)whence; return 0; }
static int fire_fs_truncate(FILE* f, int64_t length) { (void)f; (void)length; return 0; }
static int fire_fs_fileSize(const char* path, int64_t* size, FireFsStatus* st) { (void)path; (void)size; return fire_fs_none(st); }
static int fire_fs_fileTime(const char* path, int64_t* t, FireFsStatus* st) { (void)path; (void)t; return fire_fs_none(st); }
static int fire_fs_removeFile(const char* path, FireFsStatus* st) { (void)path; return fire_fs_none(st); }
static int fire_fs_copyFile(const char* from, const char* to, int overwrite, FireFsStatus* st) { (void)from; (void)to; (void)overwrite; return fire_fs_none(st); }
static int fire_fs_moveFile(const char* from, const char* to, int overwrite, FireFsStatus* st) { (void)from; (void)to; (void)overwrite; return fire_fs_none(st); }
static int fire_fs_makeDirs(const char* path, FireFsStatus* st) { (void)path; return fire_fs_none(st); }
static int fire_fs_removeDir(const char* path, int recursive, FireFsStatus* st) { (void)path; (void)recursive; return fire_fs_none(st); }
static int fire_fs_list(const char* path, const char* pattern, int recursive, int directories, FireFsList* out, FireFsStatus* st) {
    (void)path; (void)pattern; (void)recursive; (void)directories; (void)out;
    return fire_fs_none(st);
}
static int fire_fs_entryTime(const char* path, int64_t* t, FireFsStatus* st) { (void)path; (void)t; return fire_fs_none(st); }
static int fire_fs_moveDir(const char* from, const char* to, FireFsStatus* st) { (void)from; (void)to; return fire_fs_none(st); }
static int fire_fs_setCurrentDir(const char* path, FireFsStatus* st) { (void)path; return fire_fs_none(st); }
static void fire_fs_roots(FireFsList* out) { (void)out; }
static char* fire_fs_homeDir(void) { return fire_fs_dup(""); }
static char* fire_fs_currentDir(void) { return fire_fs_dup("/"); }
static char* fire_fs_tempDir(void) { return fire_fs_dup("/tmp/"); }
static char* fire_fs_fullPath(const char* path) { return fire_fs_dup(path); }

#endif
