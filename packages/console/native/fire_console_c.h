/* fire native platform layer, console part in C (the HAL of the console bridge, packages/console): the screen and the keyboard of a text console - what the
 * implementations share (the keys, UTF-8, code page 437, the ANSI colours).
 *
 * The package brings the console of each platform (`platformSources` of console.fireproj: fire_console_dos.h, _posix.h, _win32.h; `vm` and `default`:
 * fire_console_none.h), each with:
 *   int  fire_con_open(void)                      1: the program's output and input are a console (a screen and a keyboard); 0: not (redirected, or none at
 *                                                 all) - then the bridge writes plain text to standard output and reads standard input, and calls nothing else here
 *   void fire_con_write(const char* utf8, uint32_t n)   text at the cursor in the current colours (\n \r \b \t; the screen scrolls at the bottom)
 *   void fire_con_flush(void)
 *   void fire_con_size(int* cols, int* rows)      the visible part
 *   void fire_con_getCursor(int* col, int* row) / void fire_con_setCursor(int col, int row)     0-based, within the visible part
 *   void fire_con_setColor(int fg, int bg)        0..15 in the order of the CGA (FIRE_CON_BLACK ...), -1: the colour the console had at the start
 *   void fire_con_clear(void)                     the visible part in the current colours, the cursor to 0,0
 *   void fire_con_showCursor(int show)
 *   int  fire_con_keyAvailable(void)              a key waits (never blocks)
 *   int  fire_con_readKey(FireConKey* key)        the next key (blocks; the bridge only calls it when one is available): 1, or 0 when there is no more input
 *   void fire_con_close(void)                     colours, cursor and the mode of the terminal as they were (at the end of the program)
 * A new console is a new file in the package and a line in its platformSources. */
#ifndef FIRE_CONSOLE_C_H
#define FIRE_CONSOLE_C_H

#include <stdint.h>
#include <stdio.h>
#include <string.h>

/* Console.Key (packages/console/console.fire) */
enum {
    FIRE_KEY_NONE = 0, FIRE_KEY_CHARACTER = 1, FIRE_KEY_ENTER = 2, FIRE_KEY_ESCAPE = 3, FIRE_KEY_BACKSPACE = 4, FIRE_KEY_TAB = 5,
    FIRE_KEY_UP = 6, FIRE_KEY_DOWN = 7, FIRE_KEY_LEFT = 8, FIRE_KEY_RIGHT = 9, FIRE_KEY_HOME = 10, FIRE_KEY_END = 11, FIRE_KEY_PAGEUP = 12, FIRE_KEY_PAGEDOWN = 13,
    FIRE_KEY_INSERT = 14, FIRE_KEY_DELETE = 15, FIRE_KEY_F1 = 16   /* F1 .. F12: 16 .. 27 */
};
enum { FIRE_KEYMOD_SHIFT = 1, FIRE_KEYMOD_CONTROL = 2, FIRE_KEYMOD_ALT = 4 };
typedef struct FireConKey { int key; uint32_t ch; int mods; } FireConKey;   /* ch: the character the key gives (0: none) */

/* Console.Color: the 16 colours in the order of the CGA (also that of the attributes of the Windows console) */
enum { FIRE_CON_BLACK = 0, FIRE_CON_DARKBLUE, FIRE_CON_DARKGREEN, FIRE_CON_DARKCYAN, FIRE_CON_DARKRED, FIRE_CON_DARKMAGENTA, FIRE_CON_DARKYELLOW, FIRE_CON_GRAY,
       FIRE_CON_DARKGRAY, FIRE_CON_BLUE, FIRE_CON_GREEN, FIRE_CON_CYAN, FIRE_CON_RED, FIRE_CON_MAGENTA, FIRE_CON_YELLOW, FIRE_CON_WHITE };

/* The ANSI colour (0..7, bright: +8) of a colour of the CGA: the ANSI order is black, red, green, yellow, blue, magenta, cyan, white. */
static inline int fire_con_ansiOf(int c) {
    static const int map[8] = { 0, 4, 2, 6, 1, 5, 3, 7 };
    return map[c & 7] + (c & 8);
}

/* The next code point of UTF-8 text (an invalid byte: itself, as Latin-1); *i moves past it. */
static inline uint32_t fire_con_utf8Next(const char* s, uint32_t n, uint32_t* i) {
    const unsigned char* p = (const unsigned char*)s + *i;
    uint32_t left = n - *i, c = p[0];
    int len = c < 0x80 ? 1 : (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : (c >> 3) == 30 ? 4 : 0;
    int k;
    if (len == 0 || (uint32_t)len > left) { (*i)++; return c; }
    if (len > 1) {
        c &= 0x3Fu >> (len - 1);
        for (k = 1; k < len; k++) {
            if ((p[k] & 0xC0) != 0x80) { (*i)++; return p[0]; }
            c = (c << 6) | (p[k] & 0x3F);
        }
    }
    *i += (uint32_t)len;
    return c;
}

/* (code page 437: the shared table of the platform layer) */
#include "platform/std/fire_cp437.h"

/* The key of a character that came as text (a terminal, a redirected input): Enter, Tab, Backspace, Escape, a control letter, or the character itself. */
static inline void fire_con_keyOfChar(uint32_t c, FireConKey* k) {
    k->mods = 0;
    k->ch = c;
    if (c == '\r' || c == '\n') { k->key = FIRE_KEY_ENTER; k->ch = '\n'; }
    else if (c == '\t') k->key = FIRE_KEY_TAB;
    else if (c == 8 || c == 127) { k->key = FIRE_KEY_BACKSPACE; k->ch = 8; }
    else if (c == 27) k->key = FIRE_KEY_ESCAPE;
    else if (c >= 1 && c <= 26) { k->key = FIRE_KEY_CHARACTER; k->ch = 'a' + c - 1; k->mods = FIRE_KEYMOD_CONTROL; }
    else k->key = FIRE_KEY_CHARACTER;
}

#endif
