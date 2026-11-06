/* fire io package: the file system in C (the HAL of the io bridge): what the implementations share - the error codes of the language, the status of
 * an operation, a list of paths, glob matching, the lexical normalisation of a path.
 *
 * The io package brings the file system of each platform (`platformSources` of io.fireproj: fire_fs_posix.h, _win32.h, _dos.h, the boards fire_fs_board.h
 * with posix or none; `default`: fire_fs_none.h) - another package that reads files takes it with `"uses": [ "io" ]` (graphics, for its fonts). Each has, all paths
 * in UTF-8:
 *   int   fire_fs_exists(path, int* isDir)                     a file or directory is there
 *   FILE* fire_fs_open(path, mode, access, FireFsStatus*)     FileMode 0 Open, 1 Create, 2 CreateNew, 3 OpenOrCreate, 4 Append; FileAccess 0 Read, 1 Write, 2 ReadWrite
 *   int64_t fire_fs_tell(FILE*) / int fire_fs_seek(FILE*, int64_t, whence) / int fire_fs_truncate(FILE*, int64_t)    64-bit positions
 *   int   fire_fs_fileSize / fileTime (unix seconds) / removeFile / copyFile / moveFile / makeDirs / removeDir(path, recursive)
 *         / list(path, pattern, recursive, directories, FireFsList*) / entryTime / moveDir / setCurrentDir     1: done, 0: the status says why
 *   void  fire_fs_roots(FireFsList*)                           the drives ("C:\", "D:\") or "/"
 *   char* fire_fs_currentDir / tempDir / homeDir / fullPath(path)    malloc'd (the caller frees them)
 * A board without a file system defines FIRE_NO_FS (the target's `defines`); one with a file system of its own gets a file of its own in the package. */
#ifndef FIRE_FS_C_H
#define FIRE_FS_C_H

#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The error codes of the language (IoError in the IO bridge). */
enum { FIRE_FS_E_NONE = 0, FIRE_FS_E_ARGUMENT = 1, FIRE_FS_E_HANDLE = 2, FIRE_FS_E_NOFILE = 3, FIRE_FS_E_NODIR = 4, FIRE_FS_E_PERMISSION = 5, FIRE_FS_E_DENIED = 6,
       FIRE_FS_E_EXISTS = 7, FIRE_FS_E_UNSUPPORTED = 8, FIRE_FS_E_OTHER = 9 };

typedef struct FireFsStatus { int code; char message[176]; } FireFsStatus;

/* Records a failure ("%s" in `format` is the path): always 0. */
static int fire_fs_fail(FireFsStatus* st, int code, const char* format, const char* path) {
    st->code = code;
    snprintf(st->message, sizeof st->message, format, path ? path : "");
    return 0;
}

/* An error of the operating system (errno) as an error of the language: always 0. */
static int fire_fs_fromErrno(FireFsStatus* st, int err, const char* path, int parentMissing) {
    switch (err) {
        case ENOENT: return parentMissing ? fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path) : fire_fs_fail(st, FIRE_FS_E_NOFILE, "Could not find file '%s'.", path);
        case ENOTDIR: return fire_fs_fail(st, FIRE_FS_E_NODIR, "Could not find a part of the path '%s'.", path);
        case EACCES: case EPERM: case EROFS: case EISDIR: return fire_fs_fail(st, FIRE_FS_E_PERMISSION, "Access to the path '%s' is denied.", path);
        case EEXIST: return fire_fs_fail(st, FIRE_FS_E_EXISTS, "The file '%s' already exists.", path);
        default:
            st->code = FIRE_FS_E_OTHER;
            snprintf(st->message, sizeof st->message, "I/O error on '%s': %s", path ? path : "", strerror(err));
            return 0;
    }
}

/* A copy of a text (malloc). */
static char* fire_fs_dup(const char* s) {
    size_t n = strlen(s);
    char* d = (char*)malloc(n + 1);
    if (d != NULL) memcpy(d, s, n + 1);
    return d;
}

/* ---- a list of paths ---- */
typedef struct FireFsList { char** items; size_t count, cap; } FireFsList;
static void fire_fs_listAdd(FireFsList* l, const char* s) {
    char* copy;
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 16;
        char** grown = (char**)realloc(l->items, sizeof(char*) * cap);
        if (grown == NULL) return;
        l->items = grown;
        l->cap = cap;
    }
    copy = fire_fs_dup(s);
    if (copy != NULL) l->items[l->count++] = copy;
}
static void fire_fs_listFree(FireFsList* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}
static int fire_fs_byBytes(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }
static void fire_fs_listSort(FireFsList* l) { if (l->count > 1) qsort(l->items, l->count, sizeof(char*), fire_fs_byBytes); }

/* `*` and `?` against the name (like Directory.EnumerateFiles; `*.*` and `*` match everything). */
static int fire_fs_globMatch(const char* pattern, const char* name) {
    size_t p = 0, n = 0, star = (size_t)-1, mark = 0, pn = strlen(pattern), nn = strlen(name);
    if (pn == 0 || strcmp(pattern, "*") == 0 || strcmp(pattern, "*.*") == 0) return 1;
    while (n < nn) {
        if (p < pn && (pattern[p] == '?' || pattern[p] == name[n])) { p++; n++; }
        else if (p < pn && pattern[p] == '*') { star = p++; mark = n; }
        else if (star != (size_t)-1) { p = star + 1; n = ++mark; }
        else return 0;
    }
    while (p < pn && pattern[p] == '*') p++;
    return p == pn;
}

/* FIRE_FS_DRIVES: paths with drives and "\" (Windows, DOS: "C:\DIR\FILE.TXT", "/" is accepted too) - else POSIX paths. */
#if (defined(_WIN32) || defined(__DOS__) || defined(FIRE_HAL_DOS16)) && !defined(FIRE_FS_DRIVES)   /* (here: the platform file comes after this one) */
#define FIRE_FS_DRIVES 1
#endif
static const char* fire_fs_separator(void) {
#ifdef FIRE_FS_DRIVES
    return "\\";
#else
    return "/";
#endif
}
static int fire_fs_isSep(char c) {
#ifdef FIRE_FS_DRIVES
    return c == '/' || c == '\\';
#else
    return c == '/';
#endif
}

/* `dir` + separator + `name` (malloc; no second separator when `dir` ends with one). */
static char* fire_fs_join(const char* dir, const char* name) {
    size_t a = strlen(dir), b = strlen(name);
    int sep = a > 0 && !fire_fs_isSep(dir[a - 1]);
    char* out = (char*)malloc(a + b + 2);
    if (out == NULL) return NULL;
    memcpy(out, dir, a);
    if (sep) out[a++] = fire_fs_separator()[0];
    memcpy(out + a, name, b + 1);
    return out;
}

/* The folder of a path (malloc), "" when it has none. */
static char* fire_fs_parentOf(const char* path) {
    size_t n = strlen(path);
    char* out;
    while (n > 0 && !fire_fs_isSep(path[n - 1])) n--;
    while (n > 1 && fire_fs_isSep(path[n - 1])) n--;
    out = (char*)malloc(n + 1);
    if (out == NULL) return NULL;
    memcpy(out, path, n);
    out[n] = 0;
    return out;
}

/* The lexical normalisation of an absolute POSIX path (std::filesystem::lexically_normal): repeated separators collapsed, `.` dropped, `..` resolved (never above
 * the root); a trailing separator stays when the path had one. In place. */
static void fire_fs_normalize(char* path) {
    size_t n = strlen(path), r = 0, w = 0;
    int trailing = n > 1 && path[n - 1] == '/';
    size_t* starts;
    size_t depth = 0;
    if (n == 0 || path[0] != '/') return;
    starts = (size_t*)malloc(sizeof(size_t) * (n + 1));
    if (starts == NULL) return;
    path[w++] = '/';
    while (r < n) {
        size_t s, len;
        while (r < n && path[r] == '/') r++;
        s = r;
        while (r < n && path[r] != '/') r++;
        len = r - s;
        if (len == 0) break;
        if (len == 1 && path[s] == '.') continue;
        if (len == 2 && path[s] == '.' && path[s + 1] == '.') {
            if (depth > 0) w = starts[--depth];
            continue;
        }
        starts[depth++] = w;
        if (w > 1) path[w++] = '/';
        memmove(path + w, path + s, len);
        w += len;
    }
    /* (`w` points after the last segment; a segment that `..` removed leaves its separator behind) */
    if (w > 1 && path[w - 1] == '/') w--;
    if (trailing && w > 1) path[w++] = '/';
    path[w] = 0;
    free(starts);
}

#endif
