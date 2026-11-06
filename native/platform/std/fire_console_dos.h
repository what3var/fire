/* fire native platform layer, console part for DOS (dos16; dos32 later): the text mode of the PC through the BIOS - INT 10h for the screen (cursor, a character
 * with its attribute, scrolling), INT 16h for the keyboard (the enhanced functions: F11/F12, the keys of the cursor block). The text is shown in code page 437
 * (what the BIOS font has); a key's character comes back as Unicode. A console only when standard input and output are not redirected (isatty). */
#ifndef FIRE_CONSOLE_DOS_H
#define FIRE_CONSOLE_DOS_H
#include "fire_console_c.h"
#include <i86.h>
#include <bios.h>
#include <io.h>

static unsigned char fire_con_attr = 0x07, fire_con_startAttr = 0x07;
static unsigned short fire_con_startShape = 0x0607;

static unsigned char fire_con_peekb(unsigned off) { return *(unsigned char __far*)MK_FP(0x40, off); }
static unsigned short fire_con_peekw(unsigned off) { return *(unsigned short __far*)MK_FP(0x40, off); }
static void fire_con_bios(union REGS* r) { int86(0x10, r, r); }

static void fire_con_size(int* cols, int* rows) {
    unsigned char last = fire_con_peekb(0x84);   /* (the last row: EGA and later; 0 on the CGA/MDA - 25 rows) */
    *cols = fire_con_peekw(0x4A);
    *rows = last ? last + 1 : 25;
    if (*cols <= 0) *cols = 80;
}
static void fire_con_getCursor(int* col, int* row) {
    union REGS r;
    r.h.ah = 3; r.h.bh = 0;
    fire_con_bios(&r);
    *col = r.h.dl; *row = r.h.dh;
}
static void fire_con_setCursor(int col, int row) {
    union REGS r;
    int cols, rows;
    fire_con_size(&cols, &rows);
    if (col < 0) col = 0;
    if (row < 0) row = 0;
    if (col >= cols) col = cols - 1;
    if (row >= rows) row = rows - 1;
    r.h.ah = 2; r.h.bh = 0; r.h.dh = (unsigned char)row; r.h.dl = (unsigned char)col;
    fire_con_bios(&r);
}
/* the lines from `top` up by `n` (0: the whole screen empty), the new lines in the current colours */
static void fire_con_scroll(int top, int n) {
    union REGS r;
    int cols, rows;
    fire_con_size(&cols, &rows);
    r.h.ah = 6; r.h.al = (unsigned char)n; r.h.bh = fire_con_attr;
    r.h.ch = (unsigned char)top; r.h.cl = 0; r.h.dh = (unsigned char)(rows - 1); r.h.dl = (unsigned char)(cols - 1);
    fire_con_bios(&r);
}

static int fire_con_open(void) {
    union REGS r;
    if (!isatty(0) || !isatty(1)) return 0;
    r.h.ah = 8; r.h.bh = 0;   /* the attribute under the cursor: the colours DOS works with */
    fire_con_bios(&r);
    fire_con_startAttr = fire_con_attr = r.h.ah ? r.h.ah : 0x07;
    fire_con_startShape = fire_con_peekw(0x60);
    fflush(stdout);   /* (what print wrote before through DOS comes first) */
    return 1;
}
static void fire_con_write(const char* utf8, uint32_t n) {
    uint32_t i = 0;
    int col, row, cols, rows;
    fflush(stdout);
    fire_con_size(&cols, &rows);
    fire_con_getCursor(&col, &row);
    while (i < n) {
        uint32_t c = fire_con_utf8Next(utf8, n, &i);
        if (c == '\n') { col = 0; row++; }
        else if (c == '\r') col = 0;
        else if (c == '\b') { if (col > 0) col--; }
        else if (c == '\t') { col = (col / 8 + 1) * 8; if (col >= cols) { col = 0; row++; } }
        else if (c == 7) { union REGS r; r.h.ah = 0x0E; r.h.al = 7; r.h.bh = 0; fire_con_bios(&r); continue; }
        else {
            union REGS r;
            if (row >= rows) { fire_con_scroll(0, row - rows + 1); row = rows - 1; }
            fire_con_setCursor(col, row);
            r.h.ah = 9; r.h.al = fire_con_toCp437(c); r.h.bh = 0; r.h.bl = fire_con_attr; r.x.cx = 1;
            fire_con_bios(&r);
            if (++col >= cols) { col = 0; row++; }
        }
        if (row >= rows) { fire_con_scroll(0, row - rows + 1); row = rows - 1; }
    }
    fire_con_setCursor(col, row);
}
static void fire_con_flush(void) {}
static void fire_con_setColor(int fg, int bg) {
    int f = fg < 0 ? (fire_con_startAttr & 15) : (fg & 15), b = bg < 0 ? (fire_con_startAttr >> 4) & 15 : (bg & 15);
    fire_con_attr = (unsigned char)(f | (b << 4));   /* (a background of 8..15: bright on the EGA/VGA after INT 10h AX=1003h BL=0, else blinking) */
}
static void fire_con_clear(void) { fire_con_scroll(0, 0); fire_con_setCursor(0, 0); }
static void fire_con_showCursor(int show) {
    union REGS r;
    r.h.ah = 1;
    r.x.cx = show ? fire_con_startShape : 0x2000;
    if (show && (r.x.cx & 0x2000)) r.x.cx = 0x0607;
    fire_con_bios(&r);
}
static int fire_con_keyAvailable(void) { return _bios_keybrd(_NKEYBRD_READY) != 0; }
static int fire_con_readKey(FireConKey* k) {
    unsigned code = _bios_keybrd(_NKEYBRD_READ), shift = _bios_keybrd(_NKEYBRD_SHIFTSTATUS);
    unsigned scan = (code >> 8) & 0xFF, ascii = code & 0xFF;
    int mods = ((shift & 3) ? FIRE_KEYMOD_SHIFT : 0) | ((shift & 4) ? FIRE_KEYMOD_CONTROL : 0) | ((shift & 8) ? FIRE_KEYMOD_ALT : 0);
    if (ascii != 0 && ascii != 0xE0) {
        fire_con_keyOfChar(ascii == 13 ? '\n' : fire_con_fromCp437((unsigned char)ascii), k);
        if (ascii == 13 && scan == 0x1C) k->key = FIRE_KEY_ENTER;
        k->mods |= mods & ~(k->key == FIRE_KEY_CHARACTER && ascii >= 32 ? FIRE_KEYMOD_SHIFT : 0);   /* (an upper-case letter: the character says it) */
        return 1;
    }
    k->ch = 0;
    /* (the BIOS has codes of their own for a key with Ctrl, Alt or Shift: they say it too, also when the key is no longer held) */
    if ((scan >= 0x73 && scan <= 0x77) || scan == 0x84 || scan == 0x8D || scan == 0x91 || scan == 0x92 || scan == 0x93 || (scan >= 0x5E && scan <= 0x67) || scan == 0x89 || scan == 0x8A)
        mods |= FIRE_KEYMOD_CONTROL;
    else if ((scan >= 0x68 && scan <= 0x71) || scan == 0x8B || scan == 0x8C || (scan >= 0x97 && scan <= 0xA3)) mods |= FIRE_KEYMOD_ALT;
    else if ((scan >= 0x54 && scan <= 0x5D) || scan == 0x87 || scan == 0x88) mods |= FIRE_KEYMOD_SHIFT;
    k->mods = mods;
    switch (scan) {
        case 0x98: k->key = FIRE_KEY_UP; break;
        case 0xA0: k->key = FIRE_KEY_DOWN; break;
        case 0x9B: k->key = FIRE_KEY_LEFT; break;
        case 0x9D: k->key = FIRE_KEY_RIGHT; break;
        case 0x97: k->key = FIRE_KEY_HOME; break;
        case 0x9F: k->key = FIRE_KEY_END; break;
        case 0x99: k->key = FIRE_KEY_PAGEUP; break;
        case 0xA1: k->key = FIRE_KEY_PAGEDOWN; break;
        case 0xA2: k->key = FIRE_KEY_INSERT; break;
        case 0xA3: k->key = FIRE_KEY_DELETE; break;
        case 0x48: case 0x8D: k->key = FIRE_KEY_UP; break;
        case 0x50: case 0x91: k->key = FIRE_KEY_DOWN; break;
        case 0x4B: case 0x73: k->key = FIRE_KEY_LEFT; break;
        case 0x4D: case 0x74: k->key = FIRE_KEY_RIGHT; break;
        case 0x47: case 0x77: k->key = FIRE_KEY_HOME; break;
        case 0x4F: case 0x75: k->key = FIRE_KEY_END; break;
        case 0x49: case 0x84: k->key = FIRE_KEY_PAGEUP; break;
        case 0x51: case 0x76: k->key = FIRE_KEY_PAGEDOWN; break;
        case 0x52: case 0x92: k->key = FIRE_KEY_INSERT; break;
        case 0x53: case 0x93: k->key = FIRE_KEY_DELETE; break;
        case 0x85: case 0x87: case 0x89: case 0x8B: k->key = FIRE_KEY_F1 + 10; break;
        case 0x86: case 0x88: case 0x8A: case 0x8C: k->key = FIRE_KEY_F1 + 11; break;
        default:
            if (scan >= 0x3B && scan <= 0x44) k->key = FIRE_KEY_F1 + (int)(scan - 0x3B);          /* F1..F10 */
            else if (scan >= 0x54 && scan <= 0x71) k->key = FIRE_KEY_F1 + (int)((scan - 0x54) % 10);   /* with Shift, Ctrl, Alt */
            else k->key = FIRE_KEY_NONE;
    }
    return 1;
}
static void fire_con_close(void) { fire_con_attr = fire_con_startAttr; fire_con_showCursor(1); }

#endif
