/* fire io package: file system part in C on DOS (ROADMAP 13.3: dos16, dos32) - the C library of Open Watcom (stat, opendir, mkdir, _fullpath). The
 * interface: fire_fs_c.h. Paths as DOS has them: "C:\FOLDER\FILE.TXT" ("/" separates too), names in 8.3, upper or lower case alike; what comes back from the
 * system (full paths, the current directory, listings) is in upper case. Long names (LFN) go through as far as the DOS below has them. */
#ifndef FIRE_FS_DOS_H
#define FIRE_FS_DOS_H

#define FIRE_FS_DRIVES 1
#define FIRE_FS_DOS 1
#include <ctype.h>
#include <direct.h>
#include <dirent.h>
#include <dos.h>
#include <errno.h>
#include <io.h>
#include <sys/stat.h>
#include <sys/types.h>
#include "fire_fs_c.h"

/* In place: what DOS gives back is in upper case (it does not tell case apart). */
static char* fire_fs_upper(char* s) {
    char* p;
    if (s) for (p = s; *p; p++) *p = (char)toupper((unsigned char)*p);
    return s;
}

/* "C:" or "C:\" alone (stat does not see a root): the drive is there. */
static int fire_fs_isDriveRoot(const char* path) {
    return isalpha((unsigned char)path[0]) && path[1] == ':' && (path[2] == 0 || (fire_fs_isSep(path[2]) && path[3] == 0));
}
static int fire_fs_driveThere(int letter) {
    struct diskfree_t d;
    return _dos_getdiskfree((unsigned)(toupper(letter) - 'A' + 1), &d) == 0;
}

static int fire_fs_exists(const char* path, int* isDir) {
    struct stat st;
    if (fire_fs_isDriveRoot(path)) { *isDir = 1; return fire_fs_driveThere(path[0]); }
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
        case 0: how = access == 0 ? "rb" : "r+b"; break;
        case 1: case 2: how = "w+b"; break;
        case 3:
            if (!present) { FILE* made = fopen(path, "wb"); if (made) fclose(made); }
            how = access == 0 ? "rb" : "r+b";
            break;
        default: how = "ab"; break;
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

/* (positions of 32 bits: a file of DOS is at most 2 GB) */
static int64_t fire_fs_tell(FILE* f) { return (int64_t)ftell(f); }
static int fire_fs_seek(FILE* f, int64_t offset, int whence) { return fseek(f, (long)offset, whence) == 0; }
static int fire_fs_truncate(FILE* f, int64_t length) { fflush(f); return chsize(fileno(f), (long)length) == 0; }

static int fire_fs_fileSize(const char* path, int64_t* size, FireFsStatus* st) {
    struct stat s;
    if (stat(path, &s) != 0) return fire_fs_fromErrno(st, errno, path, 0);
    if (S_ISDIR(s.st_mode)) return fire_fs_fail(st, FIRE_FS_E_PERMISSION, "Access to the path '%s' is denied.", path);
    *size = (int64_t)s.st_size;
    return 1;
}
static int fire_fs_fileTime(const char* path, int64_t* unixSeconds, FireFsStatus* st) {
    struct stat s;
    if (stat(path, &s) != 0) return fire_fs_fromErrno(st, errno, path, 0);
    *unixSeconds = (int64_t)s.st_mtime;
    return 1;
}
static int fire_fs_removeFile(const char* path, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir)) return 1;
    if (isDir) return fire_fs_fail(st, FIRE_FS_E_PERMISSION, "Access to the path '%s' is denied.", path);
    if (unlink(path) != 0) return fire_fs_fromErrno(st, errno, path, 0);
    return 1;
}
/* (the chunk on the heap: the stack of a fire thread is small on DOS) */
static int fire_fs_copyBytes(const char* from, const char* to, FireFsStatus* st) {
    FILE* in;
    FILE* out;
    size_t n;
    char* chunk = (char*)malloc(4096);
    if (chunk == NULL) return fire_fs_fail(st, FIRE_FS_E_OTHER, "Out of memory (%s).", from);
    in = fopen(from, "rb");
    if (in == NULL) { free(chunk); return fire_fs_fromErrno(st, errno, from, 0); }
    out = fopen(to, "wb");
    if (out == NULL) { int err = errno; fclose(in); free(chunk); return fire_fs_fromErrno(st, err, to, 0); }
    while ((n = fread(chunk, 1, 4096, in)) > 0) {
        if (fwrite(chunk, 1, n, out) != n) { int err = errno; fclose(in); fclose(out); free(chunk); return fire_fs_fromErrno(st, err ? err : EIO, to, 0); }
    }
    fclose(in);
    free(chunk);
    if (fclose(out) != 0) return fire_fs_fromErrno(st, errno, to, 0);
    return 1;
}
static int fire_fs_copyFile(const char* from, const char* to, int overwrite, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(from, &isDir) || isDir) return fire_fs_fromErrno(st, isDir ? EACCES : ENOENT, from, 0);
    if (!overwrite && fire_fs_exists(to, &isDir)) return fire_fs_fromErrno(st, EEXIST, to, 0);
    return fire_fs_copyBytes(from, to, st);
}
static int fire_fs_moveFile(const char* from, const char* to, int overwrite, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(from, &isDir)) return fire_fs_fail(st, FIRE_FS_E_NOFILE, "Could not find file '%s'.", from);
    if (overwrite && fire_fs_exists(to, &isDir) && !isDir) unlink(to);
    if (rename(from, to) != 0) {
        if (!fire_fs_copyBytes(from, to, st)) return 0;   /* (another drive: copy, then delete) */
        unlink(from);
    }
    return 1;
}
/* Also the missing directories between ("C:\A\B\C": C:\A, C:\A\B, ...); an existing one is not an error. */
static int fire_fs_makeDirs(const char* path, FireFsStatus* st) {
    char* p = fire_fs_dup(path);
    size_t i, n, start = 1;
    int isDir = 0;
    if (p == NULL) return fire_fs_fail(st, FIRE_FS_E_OTHER, "Out of memory (%s).", path);
    n = strlen(p);
    if (n >= 2 && p[1] == ':') start = n >= 3 && fire_fs_isSep(p[2]) ? 3 : 2;   /* (not the drive itself) */
    for (i = start; i <= n; i++) {
        if (i == n || fire_fs_isSep(p[i])) {
            char keep = p[i];
            p[i] = 0;
            if (p[0] && !fire_fs_exists(p, &isDir)) {
                if (mkdir(p) != 0 && errno != EEXIST) { int err = errno; free(p); return fire_fs_fromErrno(st, err, path, 0); }
            } else if (p[0] && !isDir) { free(p); return fire_fs_fromErrno(st, EEXIST, path, 0); }
            p[i] = keep;
        }
    }
    free(p);
    return 1;
}
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
        if (stat(child, &s) == 0 && S_ISDIR(s.st_mode)) {
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
        DIR* d = opendir(path);
        int entries = 0;
        struct dirent* e;
        if (d) { while ((e = readdir(d)) != NULL) if (strcmp(e->d_name, ".") && strcmp(e->d_name, "..")) entries++; closedir(d); }
        if (entries) return fire_fs_fail(st, FIRE_FS_E_OTHER, "The directory '%s' is not empty.", path);
        return fire_fs_fromErrno(st, errno, path, 0);
    }
    return 1;
}
/* (a DOS pattern matches without case: the names are compared in upper case) */
static void fire_fs_listInto(const char* path, const char* pattern, int recursive, int directories, FireFsList* out) {
    DIR* d = opendir(path);
    struct dirent* e;
    char* up = fire_fs_upper(fire_fs_dup(pattern));
    if (d == NULL || up == NULL) { if (d) closedir(d); free(up); return; }
    while ((e = readdir(d)) != NULL) {
        char* child;
        char name[260];
        struct stat s;
        int isDir;
        if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
        strncpy(name, e->d_name, sizeof name - 1);
        name[sizeof name - 1] = 0;
        fire_fs_upper(name);
        child = fire_fs_join(path, name);
        if (child == NULL) continue;
        fire_fs_upper(child);
        if (stat(child, &s) != 0) { free(child); continue; }
        isDir = S_ISDIR(s.st_mode);
        if (isDir == directories && fire_fs_globMatch(up, name)) fire_fs_listAdd(out, child);
        if (recursive && isDir) fire_fs_listInto(child, pattern, recursive, directories, out);
        free(child);
    }
    closedir(d);
    free(up);
}
static int fire_fs_list(const char* path, const char* pattern, int recursive, int directories, FireFsList* out, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
    fire_fs_listInto(path, pattern, recursive, directories, out);
    fire_fs_listSort(out);
    return 1;
}
static int fire_fs_entryTime(const char* path, int64_t* unixSeconds, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(path, &isDir)) return fire_fs_fail(st, FIRE_FS_E_NOFILE, "Could not find file '%s'.", path);
    return fire_fs_fileTime(path, unixSeconds, st);
}
static int fire_fs_moveDir(const char* from, const char* to, FireFsStatus* st) {
    int isDir = 0;
    if (!fire_fs_exists(from, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", from);
    if (fire_fs_exists(to, &isDir)) return fire_fs_fail(st, FIRE_FS_E_EXISTS, "The target '%s' already exists.", to);
    if (rename(from, to) != 0) return fire_fs_fromErrno(st, errno, to, 0);
    return 1;
}
/* (with a drive: that drive becomes the current one as well, as `CD` and the drive letter at the prompt together) */
static int fire_fs_setCurrentDir(const char* path, FireFsStatus* st) {
    int isDir = 0;
    unsigned drives;
    if (!fire_fs_exists(path, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
    if (path[0] && path[1] == ':') _dos_setdrive((unsigned)(toupper((unsigned char)path[0]) - 'A' + 1), &drives);
    if (chdir(path) != 0) return fire_fs_fromErrno(st, errno, path, 1);
    return 1;
}
/* Directory.GetLogicalDrives: the drives from C: on that are there (A: and B: are floppies: asking them waits for a disk) - "C:\", "D:\", ... */
static void fire_fs_roots(FireFsList* out) {
    char root[4] = "C:\\";
    int letter;
    for (letter = 'C'; letter <= 'Z'; letter++)
        if (fire_fs_driveThere(letter)) { root[0] = (char)letter; fire_fs_listAdd(out, root); }
}
static char* fire_fs_homeDir(void) {
    const char* home = getenv("HOME");
    return fire_fs_upper(fire_fs_dup(home ? home : ""));
}
static char* fire_fs_currentDir(void) {
    char buffer[260];
    if (getcwd(buffer, sizeof buffer)) return fire_fs_upper(fire_fs_dup(buffer));
    return fire_fs_dup("");
}
/* With a "\" at the end (like Path.GetTempPath): TEMP, TMP, else the root of the current drive. */
static char* fire_fs_tempDir(void) {
    const char* dir = getenv("TEMP");
    char root[4] = "C:\\";
    size_t n;
    char* out;
    if (dir == NULL || !dir[0]) dir = getenv("TMP");
    if (dir == NULL || !dir[0]) { unsigned drive; _dos_getdrive(&drive); root[0] = (char)('A' + drive - 1); dir = root; }
    n = strlen(dir);
    out = (char*)malloc(n + 2);
    if (out == NULL) return NULL;
    memcpy(out, dir, n + 1);
    if (n == 0 || !fire_fs_isSep(out[n - 1])) { out[n] = '\\'; out[n + 1] = 0; }
    return fire_fs_upper(out);
}
/* Path.GetFullPath: the drive and the current directory put in front (_fullpath), "." and ".." resolved (never above the root of the drive), repeated
 * separators collapsed, in upper case, "\" as the separator. */
static char* fire_fs_fullPath(const char* path) {
    char buffer[260];
    char* out;
    size_t i, o, root;
    if (_fullpath(buffer, path, sizeof buffer) == NULL) return NULL;
    out = fire_fs_dup(buffer);
    if (out == NULL) return NULL;
    for (i = 0; out[i]; i++) if (out[i] == '/') out[i] = '\\';
    root = out[0] && out[1] == ':' ? 3 : 1;   /* ("C:\" - or "\" without a drive) */
    if (strlen(out) < root) return fire_fs_upper(out);
    o = root;
    i = root;
    while (out[i]) {
        size_t start = i, len;
        while (out[i] && out[i] != '\\') i++;
        len = i - start;
        if (len == 0 || (len == 1 && out[start] == '.')) { }
        else if (len == 2 && out[start] == '.' && out[start + 1] == '.') {
            if (o > root) { o--; while (o > root && out[o - 1] != '\\') o--; }
        } else {
            memmove(out + o, out + start, len);
            o += len;
            if (out[i]) out[o++] = '\\';
        }
        if (out[i]) i++;
    }
    if (o > root && out[o - 1] == '\\' && !fire_fs_isSep(buffer[strlen(buffer) - 1])) o--;
    out[o] = 0;
    return fire_fs_upper(out);
}

#endif
