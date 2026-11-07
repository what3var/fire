/* fire native platform layer for Windows 3.x (ROADMAP 13.7: win16; Open Watcom, the large/huge model of the 16-bit protected mode of Windows): the clock of
 * Windows (GetTickCount), sleep that keeps the messages flowing (the multitasking of Windows 3.x is cooperative: a program that does not take its messages stops
 * every other one), the start through WinMain and the output of the program.
 *   - The program starts in WinMain: the arguments come from the command line of Windows (split at spaces, "..." keeps spaces), argv[0] is the path of the
 *     program; then the generated main (fire_win16_main).
 *   - Output: a Windows program has no console. With the environment variable FIRE_STDOUT standard output goes into that file (the tests); else into a file next
 *     to the program that a window of text shows as the program goes - each time it takes its messages (Sleep, Tick of a window), at most every 100 ms; at the end
 *     the window stays until it is closed (the program's name in its title; a program that writes nothing opens none).
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

/* ---- the output window: a read-only EDIT control in a window of its own, fed from the file standard output goes to ---- */
static char g_win16Out[148];
static long g_win16Shown = 0;
static DWORD g_win16LastLook = 0;
static HWND g_win16OutWnd = 0, g_win16OutEdit = 0;
static HINSTANCE g_win16Instance = 0;   /* (the windows of the display: their class belongs to the program's instance) */
static char g_win16Path[144];

LRESULT CALLBACK __export fire_win16_outProc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_SIZE: if (g_win16OutEdit != 0) MoveWindow(g_win16OutEdit, 0, 0, LOWORD(lp), HIWORD(lp), TRUE); return 0;
    case WM_SETFOCUS: if (g_win16OutEdit != 0) SetFocus(g_win16OutEdit); return 0;
    case WM_DESTROY: g_win16OutWnd = 0; g_win16OutEdit = 0; return 0;
    }
    return DefWindowProc(h, msg, wp, lp);
}
static int fire_win16_outOpen(void) {
    WNDCLASS c;
    if (g_win16OutWnd != 0) return 1;
    memset(&c, 0, sizeof c);
    c.lpfnWndProc = fire_win16_outProc;
    c.hInstance = g_win16Instance;
    c.hCursor = LoadCursor(0, IDC_ARROW);
    c.hIcon = LoadIcon(0, IDI_APPLICATION);
    c.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    c.lpszClassName = (LPCSTR)"FireOutput16";
    RegisterClass(&c);   /* (again: it is there already) */
    g_win16OutWnd = CreateWindow((LPCSTR)"FireOutput16", (LPCSTR)g_win16Path, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 560, 360,
                                 (HWND)0, (HMENU)0, g_win16Instance, (void FAR*)0);
    if (g_win16OutWnd == 0) return 0;
    g_win16OutEdit = CreateWindow((LPCSTR)"EDIT", (LPCSTR)"", WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_AUTOHSCROLL | ES_READONLY,
                                  0, 0, 0, 0, g_win16OutWnd, (HMENU)1, g_win16Instance, (void FAR*)0);
    SendMessage(g_win16OutEdit, WM_SETFONT, (WPARAM)GetStockObject(SYSTEM_FIXED_FONT), 0);
    SendMessage(g_win16OutEdit, EM_LIMITTEXT, 0, 0);
    ShowWindow(g_win16OutWnd, SW_SHOWNORMAL);
    UpdateWindow(g_win16OutWnd);
    { RECT r; GetClientRect(g_win16OutWnd, &r); MoveWindow(g_win16OutEdit, 0, 0, r.right, r.bottom, TRUE); }
    return 1;
}
/* what the program wrote since the last look: to the end of the text (LF -> CR LF; the oldest lines go when the control is full) */
static void fire_win16_outShow(void) {
    static char chunk[512], text[1100];
    FILE* f;
    size_t n;
    if (g_win16Out[0] == 0) return;
    fflush(stdout);
    f = fopen(g_win16Out, "rb");
    if (f == NULL) return;
    if (fseek(f, g_win16Shown, SEEK_SET) == 0) {
        while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) {
            size_t i, o = 0;
            long length;
            g_win16Shown += (long)n;
            for (i = 0; i < n; i++) { if (chunk[i] == '\n') text[o++] = '\r'; text[o++] = chunk[i]; }
            text[o] = 0;
            if (!fire_win16_outOpen()) break;
            length = (long)SendMessage(g_win16OutEdit, WM_GETTEXTLENGTH, 0, 0);
            if (length > 28000) {
                SendMessage(g_win16OutEdit, EM_SETSEL, 0, MAKELPARAM(0, 8000));
                SendMessage(g_win16OutEdit, EM_REPLACESEL, 0, (LPARAM)(LPCSTR)"");
                length = (long)SendMessage(g_win16OutEdit, WM_GETTEXTLENGTH, 0, 0);
            }
            SendMessage(g_win16OutEdit, EM_SETSEL, 0, MAKELPARAM((WORD)length, (WORD)length));
            SendMessage(g_win16OutEdit, EM_REPLACESEL, 0, (LPARAM)(LPCSTR)text);
        }
    }
    fclose(f);
}

/* the messages of the program's queue (a WM_QUIT ends it) - the other programs of Windows get their turn meanwhile; new output into its window */
static void fire_win16_pump(void) {
    MSG m;
    if (g_win16Out[0] != 0 && GetTickCount() - g_win16LastLook >= 100) { g_win16LastLook = GetTickCount(); fire_win16_outShow(); }
    while (PeekMessage(&m, 0, 0, 0, PM_REMOVE)) {
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
#define FIRE_ENV_OWN_PROGRAM_PATH 1
static inline int fire_plat_programPath(char* out, size_t cap) {
    size_t n = strlen(g_win16Path);
    if (n == 0 || n >= cap) return 0;
    memcpy(out, g_win16Path, n + 1);
    return (int)n;
}

/* the end of the program: the rest of the output into its window, which stays until it is closed */
static void fire_win16_outEnd(void) {
    MSG m;
    fire_win16_outShow();
    while (g_win16OutWnd != 0 && GetMessage(&m, 0, 0, 0)) {
        TranslateMessage(&m);
        DispatchMessage(&m);
    }
    fclose(stdout);
    remove(g_win16Out);
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
    if (to != NULL && to[0] != 0) { freopen(to, "w", stdout); setvbuf(stdout, NULL, _IONBF, 0); }   /* (unbuffered: what a test sees also of a program that hangs) */
    else {
        /* (collected next to the program, shown at the end) */
        size_t n = strlen(g_win16Path);
        memcpy(g_win16Out, g_win16Path, n + 1);
        if (n > 4 && g_win16Out[n - 4] == '.') strcpy(g_win16Out + n - 4, ".OUT"); else strcat(g_win16Out, ".OUT");
        if (freopen(g_win16Out, "w", stdout) != NULL) atexit(fire_win16_outEnd);
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
