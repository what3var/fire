/* fire native platform layer, file system part in C for Windows: the wide functions of Win32 (paths are UTF-8 for the bridge, UTF-16 for the system), C stdio for
 * the open files (_wfopen). The interface: fire_fs_c.h. */
#ifndef FIRE_FS_WIN32_H
#define FIRE_FS_WIN32_H

#include <io.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <wchar.h>
#include "fire_fs_c.h"

/* UTF-8 -> UTF-16 (malloc) */
static wchar_t* fire_fs_wide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc(sizeof(wchar_t) * (size_t)(n > 0 ? n : 1));
    if (w == NULL) return NULL;
    if (n <= 0) { w[0] = 0; return w; }
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    return w;
}
/* UTF-16 -> UTF-8 (malloc) */
static char* fire_fs_narrow(const wchar_t* w) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
    char* s = (char*)malloc((size_t)(n > 0 ? n : 1));
    if (s == NULL) return NULL;
    if (n <= 0) { s[0] = 0; return s; }
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL);
    return s;
}

/* An error of Win32 as an error of the language: always 0. */
static int fire_fs_fromWin(FireFsStatus* st, DWORD err, const char* path, int parentMissing) {
    switch (err) {
        case ERROR_FILE_NOT_FOUND: return fire_fs_fromErrno(st, ENOENT, path, parentMissing);
        case ERROR_PATH_NOT_FOUND: case ERROR_INVALID_DRIVE: case ERROR_DIRECTORY: return fire_fs_fromErrno(st, ENOTDIR, path, 1);
        case ERROR_ACCESS_DENIED: case ERROR_SHARING_VIOLATION: case ERROR_WRITE_PROTECT: return fire_fs_fromErrno(st, EACCES, path, 0);
        case ERROR_FILE_EXISTS: case ERROR_ALREADY_EXISTS: return fire_fs_fromErrno(st, EEXIST, path, 0);
        case ERROR_DIR_NOT_EMPTY: return fire_fs_fail(st, FIRE_FS_E_OTHER, "The directory '%s' is not empty.", path);
        default: {
            st->code = FIRE_FS_E_OTHER;
            snprintf(st->message, sizeof st->message, "I/O error on '%s' (Windows error %lu).", path ? path : "", (unsigned long)err);
            return 0;
        }
    }
}

static int fire_fs_attributes(const char* path, DWORD* attributes) {
    wchar_t* w = fire_fs_wide(path);
    DWORD a;
    if (w == NULL) return 0;
    a = GetFileAttributesW(w);
    free(w);
    if (a == INVALID_FILE_ATTRIBUTES) return 0;
    *attributes = a;
    return 1;
}

static int fire_fs_exists(const char* path, int* isDir) {
    DWORD a;
    if (!fire_fs_attributes(path, &a)) return 0;
    *isDir = (a & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0;
    return 1;
}

static FILE* fire_fs_open(const char* path, int mode, int access, FireFsStatus* status) {
    int isDir = 0;
    int present = fire_fs_exists(path, &isDir);
    const wchar_t* how;
    wchar_t* w;
    FILE* f;
    if (present && isDir) { fire_fs_fail(status, FIRE_FS_E_PERMISSION, "Access to the path '%s' is denied.", path); return NULL; }
    if (mode == 2 && present) { fire_fs_fail(status, FIRE_FS_E_EXISTS, "The file '%s' already exists.", path); return NULL; }
    w = fire_fs_wide(path);
    if (w == NULL) { fire_fs_fail(status, FIRE_FS_E_OTHER, "Out of memory (%s).", path); return NULL; }
    switch (mode) {
        case 0: how = access == 0 ? L"rb" : L"r+b"; break;
        case 1: case 2: how = L"w+b"; break;
        case 3:
            if (!present) { FILE* made = _wfopen(w, L"wb"); if (made) fclose(made); }
            how = access == 0 ? L"rb" : L"r+b";
            break;
        default: how = L"ab"; break;
    }
    f = _wfopen(w, how);
    free(w);
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

static int64_t fire_fs_tell(FILE* f) { return _ftelli64(f); }
static int fire_fs_seek(FILE* f, int64_t offset, int whence) { return _fseeki64(f, offset, whence) == 0; }
static int fire_fs_truncate(FILE* f, int64_t length) { fflush(f); return _chsize_s(_fileno(f), length) == 0; }

static int fire_fs_data(const char* path, WIN32_FILE_ATTRIBUTE_DATA* data, FireFsStatus* st) {
    wchar_t* w = fire_fs_wide(path);
    BOOL ok;
    if (w == NULL) return fire_fs_fail(st, FIRE_FS_E_OTHER, "Out of memory (%s).", path);
    ok = GetFileAttributesExW(w, GetFileExInfoStandard, data);
    free(w);
    if (!ok) return fire_fs_fromWin(st, GetLastError(), path, 0);
    return 1;
}

static int fire_fs_fileSize(const char* path, int64_t* size, FireFsStatus* st) {
    WIN32_FILE_ATTRIBUTE_DATA d;
    if (!fire_fs_data(path, &d, st)) return 0;
    if (d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) return fire_fs_fromErrno(st, EISDIR, path, 0);
    *size = ((int64_t)d.nFileSizeHigh << 32) | (int64_t)d.nFileSizeLow;
    return 1;
}

static int fire_fs_fileTime(const char* path, int64_t* unixSeconds, FireFsStatus* st) {
    WIN32_FILE_ATTRIBUTE_DATA d;
    ULARGE_INTEGER t;
    if (!fire_fs_data(path, &d, st)) return 0;
    t.LowPart = d.ftLastWriteTime.dwLowDateTime;
    t.HighPart = d.ftLastWriteTime.dwHighDateTime;
    *unixSeconds = (int64_t)(t.QuadPart / 10000000ULL) - 11644473600LL;   /* 100 ns since 1601 -> seconds since 1970 */
    return 1;
}

/* A file that is not there is not an error. */
static int fire_fs_removeFile(const char* path, FireFsStatus* st) {
    int isDir = 0;
    wchar_t* w;
    BOOL ok;
    if (!fire_fs_exists(path, &isDir)) return 1;
    if (isDir) return fire_fs_fail(st, FIRE_FS_E_PERMISSION, "Access to the path '%s' is denied.", path);
    w = fire_fs_wide(path);
    if (w == NULL) return fire_fs_fail(st, FIRE_FS_E_OTHER, "Out of memory (%s).", path);
    ok = DeleteFileW(w);
    free(w);
    return ok ? 1 : fire_fs_fromWin(st, GetLastError(), path, 0);
}

static int fire_fs_copyFile(const char* from, const char* to, int overwrite, FireFsStatus* st) {
    wchar_t* a = fire_fs_wide(from);
    wchar_t* b = fire_fs_wide(to);
    BOOL ok = a != NULL && b != NULL && CopyFileW(a, b, overwrite ? FALSE : TRUE);
    DWORD err = GetLastError();
    free(a); free(b);
    if (ok) return 1;
    return fire_fs_fromWin(st, err, err == ERROR_FILE_NOT_FOUND ? from : to, 0);
}

static int fire_fs_moveFile(const char* from, const char* to, int overwrite, FireFsStatus* st) {
    int isDir = 0;
    wchar_t* a;
    wchar_t* b;
    BOOL ok;
    DWORD err;
    if (!fire_fs_exists(from, &isDir)) return fire_fs_fail(st, FIRE_FS_E_NOFILE, "Could not find file '%s'.", from);
    a = fire_fs_wide(from);
    b = fire_fs_wide(to);
    ok = a != NULL && b != NULL && MoveFileExW(a, b, MOVEFILE_COPY_ALLOWED | (overwrite ? MOVEFILE_REPLACE_EXISTING : 0));
    err = GetLastError();
    free(a); free(b);
    return ok ? 1 : fire_fs_fromWin(st, err, to, 0);
}

/* Also makes the missing directories between; an existing one is not an error. */
static int fire_fs_makeDirs(const char* path, FireFsStatus* st) {
    char* p = fire_fs_dup(path);
    size_t i, n;
    int isDir = 0;
    if (p == NULL) return fire_fs_fail(st, FIRE_FS_E_OTHER, "Out of memory (%s).", path);
    n = strlen(p);
    for (i = 1; i <= n; i++) {
        if (i == n || p[i] == '/' || p[i] == '\\') {
            char keep = p[i];
            p[i] = 0;
            if (!(i == 2 && p[1] == ':') && !fire_fs_exists(p, &isDir)) {   /* (not the drive "C:") */
                wchar_t* w = fire_fs_wide(p);
                BOOL ok = w != NULL && CreateDirectoryW(w, NULL);
                DWORD err = GetLastError();
                free(w);
                if (!ok && err != ERROR_ALREADY_EXISTS) { free(p); return fire_fs_fromWin(st, err, path, 0); }
            } else if (!(i == 2 && p[1] == ':') && !isDir) { free(p); return fire_fs_fromErrno(st, EEXIST, path, 0); }
            p[i] = keep;
        }
    }
    free(p);
    return 1;
}

static int fire_fs_removeTree(const char* path, FireFsStatus* st) {
    char* pattern = fire_fs_join(path, "*");
    wchar_t* w = pattern ? fire_fs_wide(pattern) : NULL;
    WIN32_FIND_DATAW f;
    HANDLE h;
    wchar_t* dir;
    BOOL ok;
    free(pattern);
    if (w == NULL) return fire_fs_fail(st, FIRE_FS_E_OTHER, "Out of memory (%s).", path);
    h = FindFirstFileW(w, &f);
    free(w);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            char* name;
            char* child;
            if (wcscmp(f.cFileName, L".") == 0 || wcscmp(f.cFileName, L"..") == 0) continue;
            name = fire_fs_narrow(f.cFileName);
            child = name ? fire_fs_join(path, name) : NULL;
            free(name);
            if (child == NULL) continue;
            if ((f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && !(f.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
                if (!fire_fs_removeTree(child, st)) { free(child); FindClose(h); return 0; }
            } else {
                wchar_t* wc = fire_fs_wide(child);
                if (wc != NULL) {
                    if (f.dwFileAttributes & FILE_ATTRIBUTE_READONLY) SetFileAttributesW(wc, f.dwFileAttributes & ~FILE_ATTRIBUTE_READONLY);
                    ok = (f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? RemoveDirectoryW(wc) : DeleteFileW(wc);
                    free(wc);
                    if (!ok) { DWORD err = GetLastError(); free(child); FindClose(h); return fire_fs_fromWin(st, err, path, 0); }
                }
            }
            free(child);
        } while (FindNextFileW(h, &f));
        FindClose(h);
    }
    dir = fire_fs_wide(path);
    ok = dir != NULL && RemoveDirectoryW(dir);
    free(dir);
    return ok ? 1 : fire_fs_fromWin(st, GetLastError(), path, 0);
}

static int fire_fs_removeDir(const char* path, int recursive, FireFsStatus* st) {
    int isDir = 0;
    wchar_t* w;
    BOOL ok;
    if (!fire_fs_exists(path, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
    if (recursive) return fire_fs_removeTree(path, st);
    w = fire_fs_wide(path);
    ok = w != NULL && RemoveDirectoryW(w);
    free(w);
    return ok ? 1 : fire_fs_fromWin(st, GetLastError(), path, 0);
}

static void fire_fs_listInto(const char* path, const char* pattern, int recursive, int directories, FireFsList* out) {
    char* all = fire_fs_join(path, "*");
    wchar_t* w = all ? fire_fs_wide(all) : NULL;
    WIN32_FIND_DATAW f;
    HANDLE h;
    free(all);
    if (w == NULL) return;
    h = FindFirstFileW(w, &f);
    free(w);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        char* name;
        char* child;
        int isDir;
        if (wcscmp(f.cFileName, L".") == 0 || wcscmp(f.cFileName, L"..") == 0) continue;
        name = fire_fs_narrow(f.cFileName);
        if (name == NULL) continue;
        child = fire_fs_join(path, name);
        if (child == NULL) { free(name); continue; }
        isDir = (f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? 1 : 0;
        if (isDir == directories && fire_fs_globMatch(pattern, name)) fire_fs_listAdd(out, child);
        if (recursive && isDir && !(f.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) fire_fs_listInto(child, pattern, recursive, directories, out);
        free(name);
        free(child);
    } while (FindNextFileW(h, &f));
    FindClose(h);
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
    wchar_t* a;
    wchar_t* b;
    BOOL ok;
    DWORD err;
    if (!fire_fs_exists(from, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", from);
    if (fire_fs_exists(to, &isDir)) return fire_fs_fail(st, FIRE_FS_E_EXISTS, "The target '%s' already exists.", to);
    a = fire_fs_wide(from);
    b = fire_fs_wide(to);
    ok = a != NULL && b != NULL && MoveFileExW(a, b, 0);
    err = GetLastError();
    free(a); free(b);
    return ok ? 1 : fire_fs_fromWin(st, err, to, 0);
}

static int fire_fs_setCurrentDir(const char* path, FireFsStatus* st) {
    int isDir = 0;
    wchar_t* w;
    BOOL ok;
    if (!fire_fs_exists(path, &isDir) || !isDir) return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
    w = fire_fs_wide(path);
    ok = w != NULL && SetCurrentDirectoryW(w);
    free(w);
    return ok ? 1 : fire_fs_fromWin(st, GetLastError(), path, 1);
}

/* Directory.GetLogicalDrives: the drives that are there. */
static void fire_fs_roots(FireFsList* out) {
    DWORD drives = GetLogicalDrives();
    int d;
    for (d = 0; d < 26; d++) {
        char root[4];
        if (!(drives & (1u << d))) continue;
        root[0] = (char)('A' + d); root[1] = ':'; root[2] = '\\'; root[3] = 0;
        fire_fs_listAdd(out, root);
    }
}

/* The user's home folder (USERPROFILE); "" when unknown. */
static char* fire_fs_homeDir(void) {
    const wchar_t* home = _wgetenv(L"USERPROFILE");
    return home ? fire_fs_narrow(home) : fire_fs_dup("");
}

static char* fire_fs_currentDir(void) {
    wchar_t buffer[4096];
    DWORD n = GetCurrentDirectoryW(4096, buffer);
    if (n == 0 || n >= 4096) return fire_fs_dup("");
    return fire_fs_narrow(buffer);
}

/* With a trailing separator (like Path.GetTempPath). */
static char* fire_fs_tempDir(void) {
    wchar_t buffer[MAX_PATH + 2];
    DWORD n = GetTempPathW(MAX_PATH + 1, buffer);
    if (n == 0 || n > MAX_PATH + 1) return fire_fs_dup("");
    return fire_fs_narrow(buffer);
}

/* Path.GetFullPath (GetFullPathNameW: `.` and `..` resolved, the separators made backslashes). */
static char* fire_fs_fullPath(const char* path) {
    wchar_t* w = fire_fs_wide(path);
    wchar_t* full;
    DWORD n;
    char* out;
    if (w == NULL) return NULL;
    n = GetFullPathNameW(w, 0, NULL, NULL);
    if (n == 0) { free(w); return fire_fs_dup(path); }
    full = (wchar_t*)malloc(sizeof(wchar_t) * (n + 1));
    if (full == NULL) { free(w); return NULL; }
    if (GetFullPathNameW(w, n + 1, full, NULL) == 0) { free(full); free(w); return fire_fs_dup(path); }
    free(w);
    out = fire_fs_narrow(full);
    free(full);
    return out;
}

#endif
