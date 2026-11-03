/* fire native platform layer for Windows: the clock, sleep and - with FIRE_THREADS - Win32 threads, slim reader/writer locks and condition variables.
 * In C (it compiles as C++ as well). */
#ifndef FIRE_PLATFORM_WIN32_H
#define FIRE_PLATFORM_WIN32_H

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <process.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include "fire_platform_c.h"

#if defined(FIRE_THREADS) && defined(__TINYC__) && !defined(FIRE_TLS_STRUCT)
/* TinyCC has no thread-local storage: the state of a thread hangs on a TLS slot of Windows instead (fire_rt.h: FIRE_TLS_STRUCT, fire_tl()) */
#define FIRE_TLS_STRUCT 1
static DWORD fire_tlsSlot = TLS_OUT_OF_INDEXES;
static inline DWORD fire_tlsIndex(void) {
    if (fire_tlsSlot == TLS_OUT_OF_INDEXES) {
        DWORD slot = TlsAlloc();
        if (InterlockedCompareExchange((volatile LONG*)&fire_tlsSlot, (LONG)slot, (LONG)TLS_OUT_OF_INDEXES) != (LONG)TLS_OUT_OF_INDEXES) TlsFree(slot);
    }
    return fire_tlsSlot;
}
static inline void* fire_tls_get(void) { return TlsGetValue(fire_tlsIndex()); }
static inline void fire_tls_set(void* p) { TlsSetValue(fire_tlsIndex(), p); }
#endif

static inline int64_t fire_plat_nowMs(void) {
    LARGE_INTEGER count, frequency;
    QueryPerformanceCounter(&count);
    QueryPerformanceFrequency(&frequency);
    return (int64_t)(count.QuadPart / (frequency.QuadPart / 1000));
}

static inline int64_t fire_plat_unixMicros(void) {
    FILETIME ft;
    ULARGE_INTEGER t;
    GetSystemTimeAsFileTime(&ft);
    t.LowPart = ft.dwLowDateTime;
    t.HighPart = ft.dwHighDateTime;
    return (int64_t)(t.QuadPart / 10) - 11644473600000000LL;   /* 100 ns since 1601 -> microseconds since 1970 */
}

static inline void fire_plat_sleepMs(int64_t ms) { Sleep(ms < 0 ? 0 : (DWORD)ms); }

FIRE_NORETURN static inline void fire_plat_exit(int code) {
#ifdef FIRE_THREADS
    _exit(code);
#else
    exit(code);
#endif
}

#ifdef FIRE_THREADS
typedef struct { SRWLOCK l; } fire_mutex;
typedef struct { CONDITION_VARIABLE c; } fire_cond;
typedef struct fire_thread { HANDLE h; } fire_thread;
typedef void (*fire_thread_fn)(void*);

static inline void fire_mutex_init(fire_mutex* m) { InitializeSRWLock(&m->l); }
static inline void fire_mutex_lock(fire_mutex* m) { AcquireSRWLockExclusive(&m->l); }
static inline void fire_mutex_unlock(fire_mutex* m) { ReleaseSRWLockExclusive(&m->l); }
static inline void fire_cond_init(fire_cond* c) { InitializeConditionVariable(&c->c); }
static inline void fire_cond_wait(fire_cond* c, fire_mutex* m) { SleepConditionVariableSRW(&c->c, &m->l, INFINITE, 0); }
static inline int fire_cond_waitFor(fire_cond* c, fire_mutex* m, int64_t ms) { return SleepConditionVariableSRW(&c->c, &m->l, ms < 0 ? 0 : (DWORD)ms, 0) != 0; }
static inline void fire_cond_notifyAll(fire_cond* c) { WakeAllConditionVariable(&c->c); }

typedef struct { fire_thread_fn fn; void* arg; } fire_thread_start_box;
static inline unsigned __stdcall fire_thread_trampoline(void* p) {
    fire_thread_start_box box = *(fire_thread_start_box*)p;
    free(p);
    box.fn(box.arg);
    return 0;
}
static inline fire_thread* fire_thread_start(fire_thread_fn fn, void* arg, uint32_t stackBytes) {
    fire_thread* t = (fire_thread*)malloc(sizeof(fire_thread));
    fire_thread_start_box* box = (fire_thread_start_box*)malloc(sizeof(fire_thread_start_box));
    if (!t || !box) { fputs("fire runtime error: Out of memory.\n", stderr); fire_plat_exit(1); }
    box->fn = fn;
    box->arg = arg;
    t->h = (HANDLE)_beginthreadex(NULL, stackBytes, fire_thread_trampoline, box, 0, NULL);
    if (!t->h) { fputs("fire runtime error: could not start a thread.\n", stderr); fire_plat_exit(1); }
    return t;
}
static inline void fire_thread_join(fire_thread* t) { WaitForSingleObject(t->h, INFINITE); CloseHandle(t->h); free(t); }
#endif

/* ---- the process (Environment): the arguments, the path of the program and the environment variables - in UTF-8, through the wide functions ---- */
static inline const char* fire_plat_name(void) { return "windows"; }

static inline int fire_plat_toUtf8(const wchar_t* w, int wn, char* out, size_t cap) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w, wn, out, (int)cap - 1, NULL, NULL);
    if (n < 0) n = 0;
    out[n] = 0;
    return n;
}

static inline wchar_t* fire_plat_toWide(const char* utf8) {
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc(sizeof(wchar_t) * (size_t)(n > 0 ? n : 1));
    if (w == NULL) return NULL;
    if (n <= 0) { w[0] = 0; return w; }
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w, n);
    return w;
}

/* The arguments without the program's name, split from the command line by the rules of the C runtime (quotes, backslashes before quotes). `main`'s argv
 * is in the ANSI code page, the command line is not. */
static inline void fire_plat_eachArg(int argc, char** argv, void (*fn)(const char* utf8, void* ctx), void* ctx) {
    const wchar_t* p = GetCommandLineW();
    size_t len = wcslen(p);
    wchar_t* arg = (wchar_t*)malloc(sizeof(wchar_t) * (len + 1));
    char* utf8 = (char*)malloc(len * 4 + 4);
    int index = 0;
    (void)argc; (void)argv;
    if (arg == NULL || utf8 == NULL) { free(arg); free(utf8); return; }
    while (*p) {
        size_t n = 0;
        int quoted = 0;
        while (*p == L' ' || *p == L'\t') p++;
        if (!*p) break;
        if (index == 0) {
            /* the program's name: up to the next blank, quotes only group (no backslash rules) */
            while (*p && (quoted || (*p != L' ' && *p != L'\t'))) { if (*p == L'"') quoted = !quoted; p++; }
            index++;
            continue;
        }
        while (*p && (quoted || (*p != L' ' && *p != L'\t'))) {
            if (*p == L'\\') {
                size_t slashes = 0;
                while (*p == L'\\') { slashes++; p++; }
                if (*p == L'"') {
                    size_t k;
                    for (k = 0; k < slashes / 2; k++) arg[n++] = L'\\';
                    if (slashes % 2) { arg[n++] = L'"'; p++; }
                } else {
                    size_t k;
                    for (k = 0; k < slashes; k++) arg[n++] = L'\\';
                }
            } else if (*p == L'"') {
                if (quoted && p[1] == L'"') { arg[n++] = L'"'; p += 2; }   /* "" inside quotes: a quote */
                else { quoted = !quoted; p++; }
            } else arg[n++] = *p++;
        }
        fire_plat_toUtf8(arg, (int)n, utf8, len * 4 + 4);
        fn(utf8, ctx);
        index++;
    }
    free(arg);
    free(utf8);
}

static inline int fire_plat_programPath(char* out, size_t cap) {
    wchar_t buf[4096];
    DWORD n = GetModuleFileNameW(NULL, buf, 4096);
    if (n == 0 || n >= 4096) return 0;
    return fire_plat_toUtf8(buf, (int)n, out, cap);
}

static inline int fire_plat_getenv(const char* name, char* out, size_t cap) {
    wchar_t* wname = fire_plat_toWide(name);
    DWORD size, n;
    wchar_t* value;
    int r;
    if (wname == NULL) return -1;
    SetLastError(0);
    size = GetEnvironmentVariableW(wname, NULL, 0);
    if (size == 0 && GetLastError() == ERROR_ENVVAR_NOT_FOUND) { free(wname); return -1; }
    value = (wchar_t*)malloc(sizeof(wchar_t) * (size + 1));
    if (value == NULL) { free(wname); return -1; }
    n = size ? GetEnvironmentVariableW(wname, value, size + 1) : 0;
    r = fire_plat_toUtf8(value, (int)n, out, cap);
    free(value);
    free(wname);
    return r;
}

static inline void fire_plat_setenv(const char* name, const char* value) {
    wchar_t* wname = fire_plat_toWide(name);
    wchar_t* wvalue = value != NULL ? fire_plat_toWide(value) : NULL;
    if (wname != NULL) SetEnvironmentVariableW(wname, wvalue);
    free(wvalue);
    free(wname);
}

static inline void fire_plat_envEach(void (*fn)(const char* name, size_t nameLen, const char* value, void* ctx), void* ctx) {
    wchar_t* block = GetEnvironmentStringsW();
    const wchar_t* e;
    if (block == NULL) return;
    for (e = block; *e; e += wcslen(e) + 1) {
        const wchar_t* eq;
        size_t len = wcslen(e);
        char* utf8;
        int n;
        if (e[0] == L'=') continue;   /* (the hidden "=C:=C:\..." entries of the current directories) */
        eq = wcschr(e, L'=');
        if (eq == NULL) continue;
        utf8 = (char*)malloc(len * 4 + 4);
        if (utf8 == NULL) break;
        n = fire_plat_toUtf8(e, (int)len, utf8, len * 4 + 4);
        {
            char* sep = strchr(utf8, '=');
            if (sep != NULL && n > 0) { *sep = 0; fn(utf8, (size_t)(sep - utf8), sep + 1, ctx); }
        }
        free(utf8);
    }
    FreeEnvironmentStringsW(block);
}

/* `Library.Load` / `Get` (docs/language/18-native-code.md): a DLL loaded at run time (a path, or a name the system finds) and a function of it; NULL when it cannot. */
static inline void* fire_plat_dlopen(const char* path) {
    wchar_t* wide = fire_plat_toWide(path);
    HMODULE h;
    if (wide == NULL) return NULL;
    h = LoadLibraryW(wide);
    free(wide);
    return (void*)h;
}
static inline void* fire_plat_dlsym(void* library, const char* name) {
    FARPROC p = library ? GetProcAddress((HMODULE)library, name) : NULL;
    void* r;
    memcpy(&r, &p, sizeof r);
    return r;
}

#endif
