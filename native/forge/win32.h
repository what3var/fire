/* native/forge/win32.h - forge-c with Windows as its host (Phase D 2/3, docs/spec/FORGE_C.md "Windows als Host"): what the builder, the projects and the rest
 * of the command line take from POSIX, in terms of Win32 and the C runtime - the folders (opendir), the files (stat, mkdir, realpath, flock), the threads
 * (pthread_create, a mutex), the time, the environment, and the programs a build runs (fwRun: CreateProcess with the output of the program read through pipes).
 * MinGW (x86_64-w64-mingw32-gcc) and MSVC: no header of POSIX is needed (MinGW's unistd.h, dirent.h and winpthreads are not used), nothing GNU-only.
 * The paths are UTF-8 with '/' inside forge-c (fwSlashes); the system gets them through the ANSI functions (a path outside the code page is not supported). */
#ifndef FORGE_WIN32_H
#define FORGE_WIN32_H
#ifdef _WIN32

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <io.h>
#include <direct.h>
#include <process.h>
#include <fcntl.h>
#include <errno.h>
#include <signal.h>
#include <time.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <sys/utime.h>
/* (the names of windows.h that are names of forge-c as well: one translation unit) */
#undef near
#undef far
#undef RegisterClass
#undef GetObject
#undef CopyFile
#undef DeleteFile
#undef MoveFile
#undef CreateFile
#undef GetMessage
#undef SendMessage
#undef PostMessage
#undef DrawText
#undef CreateWindow
#undef LoadLibrary
#undef GetClassName
#undef interface

#ifdef _MSC_VER
typedef intptr_t ssize_t;
typedef int pid_t;
#pragma warning(disable: 4996)   /* (the POSIX names of the C runtime: open, close, getcwd, ...) */
#define strncasecmp _strnicmp
#define strcasecmp _stricmp
#endif
#ifndef S_ISDIR
#define S_ISDIR(m) (((m) & _S_IFMT) == _S_IFDIR)
#endif
#ifndef S_ISREG
#define S_ISREG(m) (((m) & _S_IFMT) == _S_IFREG)
#endif
#ifndef S_ISLNK
#define S_ISLNK(m) 0
#endif
#ifndef S_IXUSR
#define S_IXUSR 0
#define S_IXGRP 0
#define S_IXOTH 0
#endif
#ifndef X_OK
#define X_OK 0   /* (Windows knows no execute bit: there is the file) */
#endif
#define lstat stat   /* (no symbolic links the builder would see) */

/* '\' -> '/' in place (the paths of the system as forge-c keeps them) */
static char* fwSlashes(char* p) { char* q; for (q = p; q && *q; q++) if (*q == '\\') *q = '/'; return p; }

/* ---- the folders (dirent) ---- */
struct dirent { char d_name[MAX_PATH]; };
typedef struct DIR { HANDLE h; WIN32_FIND_DATAA data; int first; struct dirent entry; } DIR;
static DIR* opendir(const char* path) {
    char pattern[MAX_PATH + 4];
    DIR* d;
    size_t n = strlen(path);
    if (n == 0 || n + 3 >= sizeof pattern) return NULL;
    memcpy(pattern, path, n);
    if (pattern[n - 1] != '/' && pattern[n - 1] != '\\') pattern[n++] = '/';
    pattern[n++] = '*';
    pattern[n] = 0;
    d = (DIR*)calloc(1, sizeof *d);
    if (!d) return NULL;
    d->h = FindFirstFileA(pattern, &d->data);
    if (d->h == INVALID_HANDLE_VALUE) { free(d); return NULL; }
    d->first = 1;
    return d;
}
static struct dirent* readdir(DIR* d) {
    if (!d->first && !FindNextFileA(d->h, &d->data)) return NULL;
    d->first = 0;
    strncpy(d->entry.d_name, d->data.cFileName, sizeof d->entry.d_name - 1);
    d->entry.d_name[sizeof d->entry.d_name - 1] = 0;
    return &d->entry;
}
static int closedir(DIR* d) { FindClose(d->h); free(d); return 0; }

/* ---- files ---- */
static int fwMkdir(const char* p, int mode) { (void)mode; return _mkdir(p); }
#define mkdir fwMkdir
static char* realpath(const char* path, char* resolved) {
    char* r = _fullpath(resolved, path, 4096);
    DWORD a;
    if (!r) return NULL;
    a = GetFileAttributesA(r);
    if (a == INVALID_FILE_ATTRIBUTES) { errno = ENOENT; return NULL; }
    return fwSlashes(r);
}
/* rename onto a file that is there replaces it, as on POSIX */
static int fwRename(const char* from, const char* to) { return MoveFileExA(from, to, MOVEFILE_REPLACE_EXISTING) ? 0 : -1; }
#define rename fwRename
static int utimes(const char* path, const void* times) { (void)times; return _utime(path, NULL); }
#define LOCK_SH 1
#define LOCK_EX 2
#define LOCK_NB 4
#define LOCK_UN 8
static int flock(int fd, int op) {
    HANDLE h = (HANDLE)_get_osfhandle(fd);
    OVERLAPPED o;
    memset(&o, 0, sizeof o);
    if (h == INVALID_HANDLE_VALUE) return -1;
    if (op & LOCK_UN) return UnlockFileEx(h, 0, MAXDWORD, MAXDWORD, &o) ? 0 : -1;
    return LockFileEx(h, ((op & LOCK_EX) ? LOCKFILE_EXCLUSIVE_LOCK : 0) | ((op & LOCK_NB) ? LOCKFILE_FAIL_IMMEDIATELY : 0), 0, MAXDWORD, MAXDWORD, &o) ? 0 : -1;
}
/* the program itself (/proc/self/exe) */
static const char* fwSelfPath(char* buf, size_t n) {
    DWORD k = GetModuleFileNameA(NULL, buf, (DWORD)n);
    if (k == 0 || k >= n) return NULL;
    return fwSlashes(buf);
}

/* ---- the machine, the time, the environment ---- */
#define _SC_NPROCESSORS_ONLN 84
static long sysconf(int name) { SYSTEM_INFO si; (void)name; GetSystemInfo(&si); return (long)si.dwNumberOfProcessors; }
/* the time as .NET ticks (100 ns since 0001-01-01, UTC) */
static long long fwNowTicks(void) {
    FILETIME ft;
    ULARGE_INTEGER u;
    GetSystemTimeAsFileTime(&ft);
    u.LowPart = ft.dwLowDateTime; u.HighPart = ft.dwHighDateTime;
    return (long long)u.QuadPart + 504911232000000000LL;
}
#if !defined(__MINGW32__)
struct timeval { long tv_sec; long tv_usec; };
#else
#include <sys/time.h>
#define gettimeofday fwGettimeofday
#endif
static int fwGettimeofday(struct timeval* tv, void* zone) {
    long long t = fwNowTicks() - 621355968000000000LL;   /* (since 1970) */
    (void)zone;
    tv->tv_sec = (long)(t / 10000000LL);
    tv->tv_usec = (long)((t % 10000000LL) / 10);
    return 0;
}
#if !defined(__MINGW32__)
#define gettimeofday fwGettimeofday
#endif
static struct tm* localtime_r(const time_t* t, struct tm* r) { return localtime_s(r, t) == 0 ? r : NULL; }
static struct tm* gmtime_r(const time_t* t, struct tm* r) { return gmtime_s(r, t) == 0 ? r : NULL; }
static int setenv(const char* name, const char* value, int overwrite) { (void)overwrite; return _putenv_s(name, value); }

/* ---- threads (a pthread_t is a handle, a mutex a slim reader/writer lock: PTHREAD_MUTEX_INITIALIZER works) ---- */
typedef HANDLE pthread_t;
typedef SRWLOCK pthread_mutex_t;
#define PTHREAD_MUTEX_INITIALIZER SRWLOCK_INIT
typedef struct FwThreadStart { void* (*fn)(void*); void* arg; } FwThreadStart;
static unsigned __stdcall fwThreadMain(void* p) { FwThreadStart s = *(FwThreadStart*)p; free(p); s.fn(s.arg); return 0; }
static int pthread_create(pthread_t* t, const void* attr, void* (*fn)(void*), void* arg) {
    FwThreadStart* s = (FwThreadStart*)malloc(sizeof *s);
    (void)attr;
    if (!s) return -1;
    s->fn = fn; s->arg = arg;
    *t = (HANDLE)_beginthreadex(NULL, 0, fwThreadMain, s, 0, NULL);
    if (!*t) { free(s); return -1; }
    return 0;
}
static int pthread_join(pthread_t t, void** result) { (void)result; WaitForSingleObject(t, INFINITE); CloseHandle(t); return 0; }
static int pthread_detach(pthread_t t) { CloseHandle(t); return 0; }
static int pthread_mutex_init(pthread_mutex_t* m, const void* attr) { (void)attr; InitializeSRWLock(m); return 0; }
static int pthread_mutex_lock(pthread_mutex_t* m) { AcquireSRWLockExclusive(m); return 0; }
static int pthread_mutex_unlock(pthread_mutex_t* m) { ReleaseSRWLockExclusive(m); return 0; }
static int pthread_mutex_destroy(pthread_mutex_t* m) { (void)m; return 0; }

/* ---- programs: CreateProcess ---- */
/* one argument of a command line as the C runtime of Windows splits it (CommandLineToArgvW: quotes, backslashes before a quote doubled) */
static void fwQuoteArg(FcBuf* b, const char* a) {
    const char* p;
    if (*a && !strpbrk(a, " \t\n\v\"")) { fcBufStr(b, a); return; }
    fcBufChar(b, '"');
    for (p = a;; p++) {
        int slashes = 0;
        while (*p == '\\') { slashes++; p++; }
        if (!*p) { while (slashes-- > 0) fcBufStr(b, "\\\\"); break; }
        if (*p == '"') { while (slashes-- > 0) fcBufStr(b, "\\\\"); fcBufStr(b, "\\\""); }
        else { while (slashes-- > 0) fcBufChar(b, '\\'); fcBufChar(b, *p); }
    }
    fcBufChar(b, '"');
}
/* the full path of a program: a path as it is (with .exe when that is the file), else the PATH (';') with .exe; NULL: not found (malloc'd) */
static char* fwResolveExe(const char* exe) {
    char buf[MAX_PATH * 2];
    const char* path;
    const char* p;
    DWORD a;
    if (strchr(exe, '/') || strchr(exe, '\\') || (exe[0] && exe[1] == ':')) {
        a = GetFileAttributesA(exe);
        if (a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY)) return _strdup(exe);
        snprintf(buf, sizeof buf, "%s.exe", exe);
        a = GetFileAttributesA(buf);
        return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY) ? _strdup(buf) : NULL;
    }
    path = getenv("PATH");
    for (p = path ? path : ""; p && *p;) {
        const char* e = strchr(p, ';');
        size_t n = e ? (size_t)(e - p) : strlen(p);
        int k;
        for (k = 0; k < 2 && n > 0 && n + strlen(exe) + 6 < sizeof buf; k++) {
            snprintf(buf, sizeof buf, "%.*s/%s%s", (int)n, p, exe, k == 0 ? ".exe" : "");
            a = GetFileAttributesA(buf);
            if (a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY)) return _strdup(fwSlashes(buf));
        }
        p = e ? e + 1 : NULL;
    }
    return NULL;
}
typedef struct FwPipeRead { HANDLE h; FcBuf* into; } FwPipeRead;
static unsigned __stdcall fwPipeReader(void* p) {
    FwPipeRead* r = (FwPipeRead*)p;
    char buf[8192];
    DWORD got;
    while (ReadFile(r->h, buf, sizeof buf, &got, NULL) && got > 0) fcBufPut(r->into, buf, got);
    return 0;
}
/* (the environment of a program is this process's: what a run sets is set around CreateProcess, under this lock) */
static SRWLOCK fwEnvLock = SRWLOCK_INIT;
/* Runs `exe` with the command line `arguments` (as .NET passes ProcessStartInfo.Arguments: behind the quoted program) in workDir (NULL: here); env: NAME=VALUE pairs
 * set for it (NULL-terminated, NULL: none). out/err NULL: the program has this process's streams. The exit code, -1 when it could not be started. */
static int fwRun(const char* exe, const char* arguments, const char* workDir, const char* const* env, FcBuf* out, FcBuf* err) {
    char* resolved = fwResolveExe(exe);
    FcBuf line = {0};
    STARTUPINFOA si;
    PROCESS_INFORMATION pi;
    SECURITY_ATTRIBUTES sa;
    HANDLE outRead = NULL, outWrite = NULL, errRead = NULL, errWrite = NULL;
    char* old[8];
    int i, envCount = 0;
    DWORD code = 0;
    BOOL ok;
    if (!resolved) return -1;
    fcBufStr(&line, "");
    fwQuoteArg(&line, resolved);
    if (arguments && *arguments) { fcBufChar(&line, ' '); fcBufStr(&line, arguments); }
    memset(&si, 0, sizeof si);
    si.cb = sizeof si;
    memset(&sa, 0, sizeof sa);
    sa.nLength = sizeof sa;
    sa.bInheritHandle = TRUE;
    if (out || err) {
        if (!CreatePipe(&outRead, &outWrite, &sa, 0) || !CreatePipe(&errRead, &errWrite, &sa, 0)) { free(resolved); fcBufFree(&line); return -1; }
        SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
        si.hStdOutput = outWrite;
        si.hStdError = errWrite;
    }
    AcquireSRWLockExclusive(&fwEnvLock);
    for (envCount = 0; env && env[envCount] && envCount < 8; envCount++) {
        const char* eq = strchr(env[envCount], '=');
        char name[256];
        const char* had;
        snprintf(name, sizeof name, "%.*s", eq ? (int)(eq - env[envCount]) : 0, env[envCount]);
        had = getenv(name);
        old[envCount] = had ? _strdup(had) : NULL;
        SetEnvironmentVariableA(name, eq ? eq + 1 : NULL);
    }
    ok = CreateProcessA(resolved, line.p, NULL, NULL, TRUE, 0, NULL, workDir, &si, &pi);
    for (i = 0; i < envCount; i++) {
        const char* eq = strchr(env[i], '=');
        char name[256];
        snprintf(name, sizeof name, "%.*s", eq ? (int)(eq - env[i]) : 0, env[i]);
        SetEnvironmentVariableA(name, old[i]);
        free(old[i]);
    }
    ReleaseSRWLockExclusive(&fwEnvLock);
    free(resolved);
    fcBufFree(&line);
    if (outWrite) CloseHandle(outWrite);
    if (errWrite) CloseHandle(errWrite);
    if (!ok) { if (outRead) CloseHandle(outRead); if (errRead) CloseHandle(errRead); return -1; }
    if (out || err) {
        FcBuf ignoredOut = {0}, ignoredErr = {0};
        FwPipeRead re;
        HANDLE t;
        re.h = errRead; re.into = err ? err : &ignoredErr;
        t = (HANDLE)_beginthreadex(NULL, 0, fwPipeReader, &re, 0, NULL);
        {
            FwPipeRead ro;
            ro.h = outRead; ro.into = out ? out : &ignoredOut;
            fwPipeReader(&ro);
        }
        if (t) { WaitForSingleObject(t, INFINITE); CloseHandle(t); }
        CloseHandle(outRead);
        CloseHandle(errRead);
        fcBufFree(&ignoredOut);
        fcBufFree(&ignoredErr);
    }
    WaitForSingleObject(pi.hProcess, INFINITE);
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)code;
}
/* an argv as one command line (argv[0] is the program) */
static const char* fwCommandLineOf(FcCtx* c, char* const* argv) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 1; argv[i]; i++) { if (i > 1) fcBufChar(&b, ' '); fwQuoteArg(&b, argv[i]); }
    return fcBufDone(c, &b);
}

#endif
#endif
