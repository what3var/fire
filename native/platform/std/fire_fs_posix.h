/* fire native platform layer, file system part in C on POSIX calls (stat, opendir, mkdir, ...): Linux, macOS, the BSDs, the browser (Emscripten's virtual file
 * system) and the boards whose C library has a virtual file system (ESP-IDF: SPIFFS, FAT, LittleFS). The interface: fire_fs_c.h.
 * On a board set FIRE_FS_ROOT (a string, e.g. "/spiffs"): relative paths are relative to that mounted file system (the board has no current directory worth the
 * name), and it is the root, the home and the parent of the temporary folder; FIRE_FS_SMALL_OFFSETS: positions with ftell/fseek (a C library without fseeko). */
#ifndef FIRE_FS_POSIX_H
#define FIRE_FS_POSIX_H

#include <dirent.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#include "fire_fs_c.h"

/* ESP-IDF (newlib on the VFS) has no symbolic links and no lstat: stat says the same there */
#if defined(ESP_PLATFORM) && !defined(lstat)
#define lstat stat
#endif

static int fire_fs_exists(const char* path, int* isDir) {
    struct stat st;
    if (stat(path, &st) != 0) return 0;
    *isDir = S_ISDIR(st.st_mode) ? 1 : 0;
    return 1;
}

static FILE* fire_fs_open(const char* path, int mode, int access, FireFsStatus* status) {
    int isDir = 0;
    int present = fire_fs_exists(path, &isDir);
    const char* how;
    FILE* f;
    if (present && isDir) { fire_fs_fail(status, FIRE_FS_E_PERMISSION, "Access to the path '%s' is denied.", path); return NULL; }
    if (mode == 2 && present) { fire_fs_fail(status, FIRE_FS_E_EXISTS, "The file '%s' already exists.", path); return NULL; }
    switch (mode) {
        case 0: how = access == 0 ? "rb" : "r+b"; break;                 /* Open: must exist */
        case 1: case 2: how = "w+b"; break;                              /* Create / CreateNew */
        case 3:                                                         /* OpenOrCreate */
            if (!present) { FILE* made = fopen(path, "wb"); if (made) fclose(made); }
            how = access == 0 ? "rb" : "r+b";
            break;
        default: how = "ab"; break;                                      /* Append */
    }
    f = fopen(path, how);
    if (f == NULL) {
        int err = errno, d = 0, parentMissing = 0;
        char* parent = fire_fs_parentOf(path);
        if (parent != NULL && parent[0]) parentMissing = !fire_fs_exists(parent, &d);
        free(parent);
        fire_fs_fromErrno(status, err, path, parentMissing);
        return NULL;
    }
    if (mode == 4) fseek(f, 0, SEEK_END);
    return f;
}

#ifdef FIRE_FS_SMALL_OFFSETS
static int64_t fire_fs_tell(FILE* f) { return (int64_t)ftell(f); }
static int fire_fs_seek(FILE* f, int64_t offset, int whence) { return fseek(f, (long)offset, whence) == 0; }
#else
static int64_t fire_fs_tell(FILE* f) { return (int64_t)ftello(f); }
static int fire_fs_seek(FILE* f, int64_t offset, int whence) { return fseeko(f, (off_t)offset, whence) == 0; }
#endif
static int fire_fs_truncate(FILE* f, int64_t length) {
    fflush(f);
    return ftruncate(fileno(f), (off_t)length) == 0;
}

static int fire_fs_fileSize(const char* path, int64_t* size, FireFsStatus* st) {
    struct stat s;
    if (stat(path, &s) != 0) return fire_fs_fromErrno(st, errno, path, 0);
    if (S_ISDIR(s.st_mode)) return fire_fs_fromErrno(st, EISDIR, path, 0);
    *size = (int64_t)s.st_size;
    return 1;
}

static int fire_fs_fileTime(const char* path, int64_t* unixSeconds, FireFsStatus* st) {
    struct stat s;
    if (stat(path, &s) != 0) return fire_fs_fromErrno(st, errno, path, 0);
    *unixSeconds = (int64_t)s.st_mtime;
    return 1;
}

/* A file that is not there is not an error. */
static int fire_fs_removeFile(const char* path, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir)) return 1;
    if (isDir) return fire_fs_fail(st, FIRE_FS_E_PERMISSION, "Access to the path '%s' is denied.", path);
    if (unlink(path) != 0) return fire_fs_fromErrno(st, errno, path, 0);
    return 1;
}

/* Copies the bytes (and the permissions) of a file. */
static int fire_fs_copyBytes(const char* from, const char* to, FireFsStatus* st) {
    FILE* in;
    FILE* out;
    struct stat s;
    char chunk[16384];
    size_t n;
    in = fopen(from, "rb");
    if (in == NULL) return fire_fs_fromErrno(st, errno, from, 0);
    out = fopen(to, "wb");
    if (out == NULL) { int err = errno; fclose(in); return fire_fs_fromErrno(st, err, to, 0); }
    while ((n = fread(chunk, 1, sizeof chunk, in)) > 0) {
        if (fwrite(chunk, 1, n, out) != n) { int err = errno; fclose(in); fclose(out); return fire_fs_fromErrno(st, err ? err : EIO, to, 0); }
    }
    fclose(in);
    if (fclose(out) != 0) return fire_fs_fromErrno(st, errno, to, 0);
    if (stat(from, &s) == 0) chmod(to, s.st_mode & 07777);
    return 1;
}

static int fire_fs_copyFile(const char* from, const char* to, int overwrite, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(from, &isDir) || isDir) return fire_fs_fromErrno(st, isDir ? EISDIR : ENOENT, from, 0);
    if (!overwrite && fire_fs_exists(to, &isDir)) return fire_fs_fromErrno(st, EEXIST, to, 0);
    return fire_fs_copyBytes(from, to, st);
}

static int fire_fs_moveFile(const char* from, const char* to, int overwrite, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(from, &isDir)) return fire_fs_fail(st, FIRE_FS_E_NOFILE, "Could not find file '%s'.", from);
    if (overwrite && fire_fs_exists(to, &isDir) && !isDir) unlink(to);
    if (rename(from, to) != 0) {
        /* another file system: copy, then delete */
        if (!fire_fs_copyBytes(from, to, st)) return 0;
        unlink(from);
    }
    return 1;
}

/* Also makes the missing directories between; an existing one is not an error. */
static int fire_fs_makeDirs(const char* path, FireFsStatus* st) {
    char* p = fire_fs_dup(path);
    size_t i, n;
    int isDir = 0;
    if (p == NULL) return fire_fs_fail(st, FIRE_FS_E_OTHER, "Out of memory (%s).", path);
    n = strlen(p);
    for (i = 1; i <= n; i++) {
        if (i == n || p[i] == '/') {
            char keep = p[i];
            p[i] = 0;
            if (!fire_fs_exists(p, &isDir)) {
                if (mkdir(p, 0777) != 0 && errno != EEXIST) { int err = errno; free(p); return fire_fs_fromErrno(st, err, path, 0); }
            } else if (!isDir) { free(p); return fire_fs_fromErrno(st, EEXIST, path, 0); }
            p[i] = keep;
        }
    }
    free(p);
    return 1;
}

/* Removes a directory with everything in it (symbolic links are removed, not followed). */
static int fire_fs_removeTree(const char* path, FireFsStatus* st) {
    DIR* d = opendir(path);
    struct dirent* e;
    if (d == NULL) return fire_fs_fromErrno(st, errno, path, 0);
    while ((e = readdir(d)) != NULL) {
        char* child;
        struct stat s;
        if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
        child = fire_fs_join(path, e->d_name);
        if (child == NULL) continue;
        if (lstat(child, &s) == 0 && S_ISDIR(s.st_mode)) {
            if (!fire_fs_removeTree(child, st)) { free(child); closedir(d); return 0; }
        } else if (unlink(child) != 0) { int err = errno; free(child); closedir(d); return fire_fs_fromErrno(st, err, path, 0); }
        free(child);
    }
    closedir(d);
    if (rmdir(path) != 0) return fire_fs_fromErrno(st, errno, path, 0);
    return 1;
}

static int fire_fs_removeDir(const char* path, int recursive, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
    if (recursive) return fire_fs_removeTree(path, st);
    if (rmdir(path) != 0) {
        if (errno == ENOTEMPTY || errno == EEXIST) return fire_fs_fail(st, FIRE_FS_E_OTHER, "The directory '%s' is not empty.", path);
        return fire_fs_fromErrno(st, errno, path, 0);
    }
    return 1;
}

static void fire_fs_listInto(const char* path, const char* pattern, int recursive, int directories, FireFsList* out) {
    DIR* d = opendir(path);
    struct dirent* e;
    if (d == NULL) return;   /* (a folder that may not be read is skipped, like skip_permission_denied) */
    while ((e = readdir(d)) != NULL) {
        char* child;
        struct stat s, link;
        int isDir;
        if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
        child = fire_fs_join(path, e->d_name);
        if (child == NULL) continue;
        if (stat(child, &s) != 0) { free(child); continue; }
        isDir = S_ISDIR(s.st_mode);
        if (isDir == directories && (directories || S_ISREG(s.st_mode)) && fire_fs_globMatch(pattern, e->d_name)) fire_fs_listAdd(out, child);
        /* into a folder, not through a symbolic link to one (like recursive_directory_iterator) */
        if (recursive && isDir && lstat(child, &link) == 0 && S_ISDIR(link.st_mode)) fire_fs_listInto(child, pattern, recursive, directories, out);
        free(child);
    }
    closedir(d);
}

/* The files (or, with `directories`, the directories) of a directory with their full paths, sorted by bytes. */
static int fire_fs_list(const char* path, const char* pattern, int recursive, int directories, FireFsList* out, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
    fire_fs_listInto(path, pattern, recursive, directories, out);
    fire_fs_listSort(out);
    return 1;
}

/* The time of the last change of a file or a directory. */
static int fire_fs_entryTime(const char* path, int64_t* unixSeconds, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir)) return fire_fs_fail(st, FIRE_FS_E_NOFILE, "Could not find file '%s'.", path);
    return fire_fs_fileTime(path, unixSeconds, st);
}

/* Directory.Move: the target must not exist. */
static int fire_fs_moveDir(const char* from, const char* to, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(from, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", from);
    if (fire_fs_exists(to, &isDir)) return fire_fs_fail(st, FIRE_FS_E_EXISTS, "The target '%s' already exists.", to);
    if (rename(from, to) != 0) return fire_fs_fromErrno(st, errno, to, 0);
    return 1;
}

static int fire_fs_setCurrentDir(const char* path, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
    if (chdir(path) != 0) return fire_fs_fromErrno(st, errno, path, 1);
    return 1;
}

#ifndef FIRE_FS_ROOT
/* the root, the home and the parent of the temporary folder of a hosted system */
#define FIRE_FS_HOSTED 1
#define FIRE_FS_ROOT "/"
#endif

/* Directory.GetLogicalDrives: "/" (a board: its mounted file system). */
static void fire_fs_roots(FireFsList* out) { fire_fs_listAdd(out, FIRE_FS_ROOT); }

/* The user's home folder (HOME); "" when unknown (a board: its root). */
static char* fire_fs_homeDir(void) {
    const char* home = getenv("HOME");
#ifdef FIRE_FS_HOSTED
    return fire_fs_dup(home ? home : "");
#else
    return fire_fs_dup(home ? home : FIRE_FS_ROOT);
#endif
}

static char* fire_fs_currentDir(void) {
    char buffer[4096];
#ifdef FIRE_FS_HOSTED
    if (getcwd(buffer, sizeof buffer)) return fire_fs_dup(buffer);
    return fire_fs_dup("");
#else
    if (getcwd(buffer, sizeof buffer) && buffer[0] == '/' && buffer[1]) return fire_fs_dup(buffer);
    return fire_fs_dup(FIRE_FS_ROOT);
#endif
}

/* With a trailing separator (like Path.GetTempPath): TMPDIR, TMP, TEMP, TEMPDIR, else /tmp (a board: <root>/tmp/). */
static char* fire_fs_tempDir(void) {
#ifdef FIRE_FS_HOSTED
    static const char* const names[] = {"TMPDIR", "TMP", "TEMP", "TEMPDIR"};
    const char* dir = NULL;
    size_t i, n;
    char* out;
    for (i = 0; i < 4 && dir == NULL; i++) { dir = getenv(names[i]); if (dir != NULL && !dir[0]) dir = NULL; }
    if (dir == NULL) dir = "/tmp";
    n = strlen(dir);
    out = (char*)malloc(n + 2);
    if (out == NULL) return NULL;
    memcpy(out, dir, n + 1);
    if (n == 0 || out[n - 1] != '/') { out[n] = '/'; out[n + 1] = 0; }
    return out;
#else
    return fire_fs_join(FIRE_FS_ROOT, "tmp/");
#endif
}

/* Path.GetFullPath: an absolute path with `.` and `..` resolved and repeated separators collapsed (no symbolic links resolved). */
static char* fire_fs_fullPath(const char* path) {
    char* out;
    if (path[0] == '/') out = fire_fs_dup(path);
    else {
        char* base = fire_fs_currentDir();
        if (base == NULL) return NULL;
        out = fire_fs_join(base[0] ? base : "/", path);
        free(base);
    }
    if (out != NULL) fire_fs_normalize(out);
    return out;
}

#endif
