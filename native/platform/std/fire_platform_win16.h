/* fire native platform layer for Windows 3.x (ROADMAP 13.7: win16; Open Watcom, the large/huge model of the 16-bit protected mode of Windows): the clock of
 * Windows (GetTickCount), sleep that keeps the messages flowing (the multitasking of Windows 3.x is cooperative: a program that does not take its messages stops
 * every other one), the start through WinMain and the output of the program.
 *   - The program starts in WinMain: the arguments come from the command line of Windows (split at spaces, "..." keeps spaces), argv[0] is the path of the
 *     program; then the generated main (fire_win16_main).
 *   - Output: a Windows program has no console. With the environment variable FIRE_STDOUT standard output goes into that file (the tests); else it is collected
 *     and shown in a window of text when the program ends (the program's name in its title).
 * No fire threads: Windows 3.x has one thread per program. */
#ifndef FIRE_PLATFORM_WIN16_H
#define FIRE_PLATFORM_WIN16_H

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "fire_platform_c.h"

/* A monotonic clock in milliseconds: GetTickCount (milliseconds since Windows started; 32 bits, carried over its end after 49 days). */
static inline int64_t fire_plat_nowMs(void) {
    static DWORD last = 0;
    static int64_t base = 0;
    DWORD now = GetTickCount();
    if (now < last) base += (int64_t)0x100000000LL;
    last = now;
    return base + (int64_t)now;
}

/* The wall clock: the local time of DOS under Windows, no zone; time() anchored to the clock above. */
static inline int64_t fire_plat_unixMicros(void) {
    static int64_t anchor = 0;
    static int anchored = 0;
    int64_t seconds = (int64_t)time(NULL) * 1000000;
    int64_t ms = fire_plat_nowMs() * 1000;
    int64_t drift = seconds - (anchor + ms);
    if (!anchored || drift > 1000000 || drift < -1000000) { anchor = seconds - ms; anchored = 1; }
    return anchor + ms;
}

/* the messages of the program's queue (a WM_QUIT ends it) - the other programs of Windows get their turn meanwhile */
static void fire_win16_pump(void) {
    MSG m;
    while (PeekMessage(&m, NULL, 0, 0, PM_REMOVE)) {
        if (m.message == WM_QUIT) exit((int)m.wParam);
        TranslateMessage(&m);
        DispatchMessage(&m);
    }
}
static inline void fire_plat_sleepMs(int64_t ms) {
    int64_t end = fire_plat_nowMs() + ms;
    do {
        fire_win16_pump();
        Yield();
    } while (fire_plat_nowMs() < end);
}

FIRE_NORETURN static inline void fire_plat_exit(int code) {
    exit(code);
}

/* ---- the start: WinMain ---- */
static char g_win16Path[144];
static HINSTANCE g_win16Instance = 0;   /* (the windows of the display: their class belongs to the program's instance) */
static char g_win16Out[148];
#define FIRE_ENV_OWN_PROGRAM_PATH 1
static inline int fire_plat_programPath(char* out, size_t cap) {
    size_t n = strlen(g_win16Path);
    if (n == 0 || n >= cap) return 0;
    memcpy(out, g_win16Path, n + 1);
    return (int)n;
}

/* the collected output in a window of text (at most what a message box shows) */
static void fire_win16_showOutput(void) {
    static char text[4096];
    FILE* f;
    size_t n;
    fflush(stdout);
    if (g_win16Out[0] == 0) return;
    f = fopen(g_win16Out, "r");
    if (f == NULL) return;
    n = fread(text, 1, sizeof text - 1, f);
    fclose(f);
    remove(g_win16Out);
    text[n] = 0;
    if (n > 0) MessageBox(NULL, text, g_win16Path, MB_OK | MB_ICONINFORMATION);
}

int fire_win16_main(int argc, char** argv);

int PASCAL WinMain(HINSTANCE instance, HINSTANCE previous, LPSTR commandLine, int show) {
    static char line[260];
    static char* argv[33];
    int argc = 0;
    char* p;
    const char* to = getenv("FIRE_STDOUT");
    (void)previous; (void)show;
    g_win16Instance = instance;
    GetModuleFileName(instance, g_win16Path, sizeof g_win16Path);
    if (to != NULL && to[0] != 0) freopen(to, "w", stdout);
    else {
        /* (collected next to the program, shown at the end) */
        size_t n = strlen(g_win16Path);
        memcpy(g_win16Out, g_win16Path, n + 1);
        if (n > 4 && g_win16Out[n - 4] == '.') strcpy(g_win16Out + n - 4, ".OUT"); else strcat(g_win16Out, ".OUT");
        if (freopen(g_win16Out, "w", stdout) != NULL) atexit(fire_win16_showOutput);
        else g_win16Out[0] = 0;
    }
    argv[argc++] = g_win16Path;
    strncpy(line, commandLine ? commandLine : "", sizeof line - 1);
    p = line;
    while (*p && argc < 32) {
        while (*p == ' ' || *p == '\t') p++;
        if (!*p) break;
        if (*p == '"') {
            argv[argc++] = ++p;
            while (*p && *p != '"') p++;
        } else {
            argv[argc++] = p;
            while (*p && *p != ' ' && *p != '\t') p++;
        }
        if (*p) *p++ = 0;
    }
    argv[argc] = NULL;
    return fire_win16_main(argc, argv);
}

#define FIRE_PLAT_NAME "win16"
#include "fire_env_dos.h"   /* (the process: arguments, environment variables - the C library of Open Watcom as on DOS) */

#endif
