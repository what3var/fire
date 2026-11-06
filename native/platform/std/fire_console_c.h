/* fire native platform layer, console part in C (the HAL of the console bridge, packages/console): the screen and the keyboard of a text console - what the
 * implementations share (the keys, UTF-8, code page 437, the ANSI colours).
 *
 * A platform package offers `fire_console.h` (FIRE_PLATFORM_CONSOLE_C_HEADER points to it) with:
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
 * A new console is a new file in std/ and a line in the fire_console.h of the platform. */
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

/* Code page 437 (the character set of the PC's text mode): the upper half as Unicode. The lower half is ASCII. */
static const uint16_t fire_con_cp437[128] = {
    0x00C7, 0x00FC, 0x00E9, 0x00E2, 0x00E4, 0x00E0, 0x00E5, 0x00E7, 0x00EA, 0x00EB, 0x00E8, 0x00EF, 0x00EE, 0x00EC, 0x00C4, 0x00C5,
    0x00C9, 0x00E6, 0x00C6, 0x00F4, 0x00F6, 0x00F2, 0x00FB, 0x00F9, 0x00FF, 0x00D6, 0x00DC, 0x00A2, 0x00A3, 0x00A5, 0x20A7, 0x0192,
    0x00E1, 0x00ED, 0x00F3, 0x00FA, 0x00F1, 0x00D1, 0x00AA, 0x00BA, 0x00BF, 0x2310, 0x00AC, 0x00BD, 0x00BC, 0x00A1, 0x00AB, 0x00BB,
    0x2591, 0x2592, 0x2593, 0x2502, 0x2524, 0x2561, 0x2562, 0x2556, 0x2555, 0x2563, 0x2551, 0x2557, 0x255D, 0x255C, 0x255B, 0x2510,
    0x2514, 0x2534, 0x252C, 0x251C, 0x2500, 0x253C, 0x255E, 0x255F, 0x255A, 0x2554, 0x2569, 0x2566, 0x2560, 0x2550, 0x256C, 0x2567,
    0x2568, 0x2564, 0x2565, 0x2559, 0x2558, 0x2552, 0x2553, 0x256B, 0x256A, 0x2518, 0x250C, 0x2588, 0x2584, 0x258C, 0x2590, 0x2580,
    0x03B1, 0x00DF, 0x0393, 0x03C0, 0x03A3, 0x03C3, 0x00B5, 0x03C4, 0x03A6, 0x0398, 0x03A9, 0x03B4, 0x221E, 0x03C6, 0x03B5, 0x2229,
    0x2261, 0x00B1, 0x2265, 0x2264, 0x2320, 0x2321, 0x00F7, 0x2248, 0x00B0, 0x2219, 0x00B7, 0x221A, 0x207F, 0x00B2, 0x25A0, 0x00A0 };
/* A code point as a byte of code page 437 ('?' when it has none). */
static inline unsigned char fire_con_toCp437(uint32_t c) {
    int i;
    if (c < 0x80) return (unsigned char)c;
    for (i = 0; i < 128; i++) if (fire_con_cp437[i] == c) return (unsigned char)(0x80 + i);
    return '?';
}
static inline uint32_t fire_con_fromCp437(unsigned char b) { return b < 0x80 ? b : fire_con_cp437[b - 0x80]; }

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
