/* fire native platform layer, console part for Windows: the console API of Win32 (also on the old Windows versions - no escape sequences): the attributes of
 * the console are the colours in the order of the CGA, the text goes as UTF-16 (WriteConsoleW), the keys come from ReadConsoleInputW. A console only when
 * standard input and output are one (GetConsoleMode). */
#ifndef FIRE_CONSOLE_WIN32_H
#define FIRE_CONSOLE_WIN32_H
#include "fire_console_c.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <stdlib.h>

static HANDLE fire_con_in = NULL, fire_con_outH = NULL;
static WORD fire_con_startAttr = 7, fire_con_attr = 7;
static CONSOLE_CURSOR_INFO fire_con_startCursor;
static int fire_con_isOpen = 0;

static void fire_con_close(void) {
    if (!fire_con_isOpen) return;
    SetConsoleTextAttribute(fire_con_outH, fire_con_startAttr);
    SetConsoleCursorInfo(fire_con_outH, &fire_con_startCursor);
    fire_con_isOpen = 0;
}
static int fire_con_open(void) {
    DWORD mode;
    CONSOLE_SCREEN_BUFFER_INFO info;
    fire_con_in = GetStdHandle(STD_INPUT_HANDLE);
    fire_con_outH = GetStdHandle(STD_OUTPUT_HANDLE);
    if (!GetConsoleMode(fire_con_in, &mode) || !GetConsoleMode(fire_con_outH, &mode) || !GetConsoleScreenBufferInfo(fire_con_outH, &info)) return 0;
    fire_con_startAttr = fire_con_attr = info.wAttributes;
    GetConsoleCursorInfo(fire_con_outH, &fire_con_startCursor);
    fire_con_isOpen = 1;
    atexit(fire_con_close);
    fflush(stdout);
    return 1;
}
static void fire_con_write(const char* utf8, uint32_t n) {
    wchar_t chunk[256];
    uint32_t i = 0;
    DWORD done;
    fflush(stdout);
    while (i < n) {
        int k = 0;
        while (i < n && k < 254) {
            uint32_t c = fire_con_utf8Next(utf8, n, &i);
            if (c >= 0x10000) { c -= 0x10000; chunk[k++] = (wchar_t)(0xD800 + (c >> 10)); chunk[k++] = (wchar_t)(0xDC00 + (c & 0x3FF)); }
            else chunk[k++] = (wchar_t)c;
        }
        WriteConsoleW(fire_con_outH, chunk, (DWORD)k, &done, NULL);
    }
}
static void fire_con_flush(void) {}
static void fire_con_size(int* cols, int* rows) {
    CONSOLE_SCREEN_BUFFER_INFO info;
    if (GetConsoleScreenBufferInfo(fire_con_outH, &info)) { *cols = info.srWindow.Right - info.srWindow.Left + 1; *rows = info.srWindow.Bottom - info.srWindow.Top + 1; }
    else { *cols = 80; *rows = 25; }
}
static void fire_con_getCursor(int* col, int* row) {
    CONSOLE_SCREEN_BUFFER_INFO info;
    *col = 0; *row = 0;
    if (GetConsoleScreenBufferInfo(fire_con_outH, &info)) { *col = info.dwCursorPosition.X - info.srWindow.Left; *row = info.dwCursorPosition.Y - info.srWindow.Top; }
}
static void fire_con_setCursor(int col, int row) {
    CONSOLE_SCREEN_BUFFER_INFO info;
    COORD at;
    if (!GetConsoleScreenBufferInfo(fire_con_outH, &info)) return;
    at.X = (SHORT)(info.srWindow.Left + (col < 0 ? 0 : col));
    at.Y = (SHORT)(info.srWindow.Top + (row < 0 ? 0 : row));
    if (at.X > info.srWindow.Right) at.X = info.srWindow.Right;
    if (at.Y > info.srWindow.Bottom) at.Y = info.srWindow.Bottom;
    SetConsoleCursorPosition(fire_con_outH, at);
}
static void fire_con_setColor(int fg, int bg) {
    WORD f = (WORD)(fg < 0 ? (fire_con_startAttr & 15) : (fg & 15)), b = (WORD)(bg < 0 ? (fire_con_startAttr >> 4) & 15 : (bg & 15));
    fire_con_attr = (WORD)(f | (b << 4));
    SetConsoleTextAttribute(fire_con_outH, fire_con_attr);
}
static void fire_con_clear(void) {
    CONSOLE_SCREEN_BUFFER_INFO info;
    COORD at;
    DWORD done;
    SHORT y;
    if (!GetConsoleScreenBufferInfo(fire_con_outH, &info)) return;
    for (y = info.srWindow.Top; y <= info.srWindow.Bottom; y++) {
        DWORD width = (DWORD)(info.srWindow.Right - info.srWindow.Left + 1);
        at.X = info.srWindow.Left; at.Y = y;
        FillConsoleOutputCharacterW(fire_con_outH, L' ', width, at, &done);
        FillConsoleOutputAttribute(fire_con_outH, fire_con_attr, width, at, &done);
    }
    fire_con_setCursor(0, 0);
}
static void fire_con_showCursor(int show) {
    CONSOLE_CURSOR_INFO c = fire_con_startCursor;
    c.bVisible = show ? TRUE : FALSE;
    SetConsoleCursorInfo(fire_con_outH, &c);
}
/* the events before the next key that is pressed are dropped (releases, the mouse, a change of the size) */
static int fire_con_keyAvailable(void) {
    INPUT_RECORD rec;
    DWORD n;
    while (PeekConsoleInputW(fire_con_in, &rec, 1, &n) && n == 1) {
        if (rec.EventType == KEY_EVENT && rec.Event.KeyEvent.bKeyDown) {
            WORD vk = rec.Event.KeyEvent.wVirtualKeyCode;
            if (vk != VK_SHIFT && vk != VK_CONTROL && vk != VK_MENU && vk != VK_CAPITAL && vk != VK_NUMLOCK && vk != VK_SCROLL) return 1;
        }
        ReadConsoleInputW(fire_con_in, &rec, 1, &n);
    }
    return 0;
}
static int fire_con_readKey(FireConKey* k) {
    INPUT_RECORD rec;
    DWORD n;
    for (;;) {
        KEY_EVENT_RECORD* e;
        DWORD st;
        WORD vk;
        if (!ReadConsoleInputW(fire_con_in, &rec, 1, &n) || n != 1) return 0;
        if (rec.EventType != KEY_EVENT || !rec.Event.KeyEvent.bKeyDown) continue;
        e = &rec.Event.KeyEvent;
        vk = e->wVirtualKeyCode;
        if (vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_CAPITAL || vk == VK_NUMLOCK || vk == VK_SCROLL) continue;
        st = e->dwControlKeyState;
        k->mods = ((st & SHIFT_PRESSED) ? FIRE_KEYMOD_SHIFT : 0) | ((st & (LEFT_CTRL_PRESSED | RIGHT_CTRL_PRESSED)) ? FIRE_KEYMOD_CONTROL : 0)
                | ((st & (LEFT_ALT_PRESSED | RIGHT_ALT_PRESSED)) ? FIRE_KEYMOD_ALT : 0);
        k->ch = 0;
        switch (vk) {
            case VK_UP: k->key = FIRE_KEY_UP; return 1;
            case VK_DOWN: k->key = FIRE_KEY_DOWN; return 1;
            case VK_LEFT: k->key = FIRE_KEY_LEFT; return 1;
            case VK_RIGHT: k->key = FIRE_KEY_RIGHT; return 1;
            case VK_HOME: k->key = FIRE_KEY_HOME; return 1;
            case VK_END: k->key = FIRE_KEY_END; return 1;
            case VK_PRIOR: k->key = FIRE_KEY_PAGEUP; return 1;
            case VK_NEXT: k->key = FIRE_KEY_PAGEDOWN; return 1;
            case VK_INSERT: k->key = FIRE_KEY_INSERT; return 1;
            case VK_DELETE: k->key = FIRE_KEY_DELETE; return 1;
            default: break;
        }
        if (vk >= VK_F1 && vk <= VK_F12) { k->key = FIRE_KEY_F1 + (vk - VK_F1); return 1; }
        if (e->uChar.UnicodeChar != 0) {
            int mods = k->mods;
            fire_con_keyOfChar(e->uChar.UnicodeChar, k);
            k->mods |= mods & ~(k->key == FIRE_KEY_CHARACTER && e->uChar.UnicodeChar >= 32 ? FIRE_KEYMOD_SHIFT : 0);
            return 1;
        }
    }
}

#endif
