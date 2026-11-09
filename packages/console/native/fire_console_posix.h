/* fire console package: the console for POSIX terminals (Linux, macOS, the BSDs): ANSI escape sequences for the cursor and the colours, termios for the
 * keyboard - raw only while a key is read or looked for (the terminal echoes and edits lines as usual in between), the keys of the cursor block and F1..F12 from
 * their sequences (xterm, the Linux console, VT220). A console only when standard input and output are terminals. */
#ifndef FIRE_CONSOLE_POSIX_H
#define FIRE_CONSOLE_POSIX_H
#include "fire_console_c.h"
#include <poll.h>
#include <stdlib.h>
#include <sys/ioctl.h>
#include <termios.h>
#include <unistd.h>

static struct termios fire_con_cooked;
static int fire_con_isOpen = 0, fire_con_isRaw = 0, fire_con_hidden = 0, fire_con_colored = 0;
static unsigned char fire_con_pending[16];   /* bytes read ahead (the rest of a sequence that was no key) */
static int fire_con_pendingCount = 0;

static void fire_con_out(const char* s) { fputs(s, stdout); }
static void fire_con_raw(int on) {
    if (on == fire_con_isRaw) return;
    if (on) {
        struct termios t = fire_con_cooked;
        fflush(stdout);
        t.c_lflag &= ~(tcflag_t)(ICANON | ECHO);
        t.c_iflag &= ~(tcflag_t)(IXON | ICRNL);
        t.c_cc[VMIN] = 1; t.c_cc[VTIME] = 0;
        tcsetattr(0, TCSANOW, &t);
    } else tcsetattr(0, TCSANOW, &fire_con_cooked);
    fire_con_isRaw = on;
}
/* a byte of input within `ms` milliseconds (-1: none; ms < 0 waits) */
static int fire_con_byte(int ms) {
    unsigned char b;
    if (fire_con_pendingCount > 0) {
        int c = fire_con_pending[0];
        memmove(fire_con_pending, fire_con_pending + 1, (size_t)--fire_con_pendingCount);
        return c;
    }
    if (ms >= 0) {
        struct pollfd p;
        p.fd = 0; p.events = POLLIN; p.revents = 0;
        if (poll(&p, 1, ms) <= 0) return -1;
    }
    return read(0, &b, 1) == 1 ? b : -1;
}
static void fire_con_unread(int c) { if (c >= 0 && fire_con_pendingCount < (int)sizeof fire_con_pending) fire_con_pending[fire_con_pendingCount++] = (unsigned char)c; }

static void fire_con_close(void) {
    if (!fire_con_isOpen) return;
    fire_con_raw(0);
    if (fire_con_colored) fire_con_out("\x1b[0m");
    if (fire_con_hidden) fire_con_out("\x1b[?25h");
    fflush(stdout);
    fire_con_isOpen = 0;
}
static int fire_con_open(void) {
    const char* term = getenv("TERM");
    if (!isatty(0) || !isatty(1) || (term && strcmp(term, "dumb") == 0)) return 0;
    if (tcgetattr(0, &fire_con_cooked) != 0) return 0;
    fire_con_isOpen = 1;
    atexit(fire_con_close);
    return 1;
}
static void fire_con_write(const char* utf8, uint32_t n) { fwrite(utf8, 1, n, stdout); }
static void fire_con_flush(void) { fflush(stdout); }
static void fire_con_size(int* cols, int* rows) {
    struct winsize w;
    if (ioctl(1, TIOCGWINSZ, &w) == 0 && w.ws_col > 0 && w.ws_row > 0) { *cols = w.ws_col; *rows = w.ws_row; }
    else { *cols = 80; *rows = 24; }
}
/* the terminal answers "ESC [ row ; col R" to "ESC [ 6 n" */
static void fire_con_getCursor(int* col, int* row) {
    int was = fire_con_isRaw, c, r = 0, k = 0, state = 0;
    *col = 0; *row = 0;
    fire_con_raw(1);
    fire_con_out("\x1b[6n");
    fflush(stdout);
    while ((c = fire_con_byte(200)) >= 0) {
        if (state == 0) { if (c == 27) state = 1; else fire_con_unread(c); }
        else if (state == 1) state = c == '[' ? 2 : 0;
        else if (state == 2) { if (c >= '0' && c <= '9') r = r * 10 + (c - '0'); else if (c == ';') state = 3; else break; }
        else { if (c >= '0' && c <= '9') k = k * 10 + (c - '0'); else { if (c == 'R') { *row = r > 0 ? r - 1 : 0; *col = k > 0 ? k - 1 : 0; } break; } }
    }
    fire_con_raw(was);
}
static void fire_con_setCursor(int col, int row) { printf("\x1b[%d;%dH", (row < 0 ? 0 : row) + 1, (col < 0 ? 0 : col) + 1); }
static void fire_con_setColor(int fg, int bg) {
    fire_con_out("\x1b[0m");
    if (fg >= 0) { int a = fire_con_ansiOf(fg); printf("\x1b[%dm", (a & 8 ? 90 : 30) + (a & 7)); }
    if (bg >= 0) { int a = fire_con_ansiOf(bg); printf("\x1b[%dm", (a & 8 ? 100 : 40) + (a & 7)); }
    fire_con_colored = fg >= 0 || bg >= 0;
}
static void fire_con_clear(void) { fire_con_out("\x1b[2J\x1b[H"); }
static void fire_con_showCursor(int show) { fire_con_out(show ? "\x1b[?25h" : "\x1b[?25l"); fire_con_hidden = !show; }
static int fire_con_keyAvailable(void) {
    int was = fire_con_isRaw, c;
    if (fire_con_pendingCount > 0) return 1;
    fire_con_raw(1);
    c = fire_con_byte(0);
    fire_con_unread(c);
    fire_con_raw(was);
    return c >= 0;
}
/* the modifier of a sequence ("1;5A": 5): 1 + Shift 1, Alt 2, Ctrl 4 */
static int fire_con_seqMods(int m) {
    m = m > 1 ? m - 1 : 0;
    return ((m & 1) ? FIRE_KEYMOD_SHIFT : 0) | ((m & 2) ? FIRE_KEYMOD_ALT : 0) | ((m & 4) ? FIRE_KEYMOD_CONTROL : 0);
}
static int fire_con_readKey(FireConKey* k) {
    int was = fire_con_isRaw, c;
    fire_con_raw(1);
    c = fire_con_byte(-1);
    k->key = FIRE_KEY_NONE; k->ch = 0; k->mods = 0;
    if (c < 0) { fire_con_raw(was); return 0; }
    if (c == 27) {
        int d = fire_con_byte(50);
        if (d < 0) fire_con_keyOfChar(27, k);
        else if (d == '[' || d == 'O') {
            int num = 0, mod = 0, e, field = 0;
            while ((e = fire_con_byte(50)) >= 0 && ((e >= '0' && e <= '9') || e == ';')) {
                if (e == ';') field++;
                else if (field == 0) num = num * 10 + (e - '0');
                else mod = mod * 10 + (e - '0');
            }
            k->mods = fire_con_seqMods(mod);
            switch (e) {
                case 'A': k->key = FIRE_KEY_UP; break;
                case 'B': k->key = FIRE_KEY_DOWN; break;
                case 'C': k->key = FIRE_KEY_RIGHT; break;
                case 'D': k->key = FIRE_KEY_LEFT; break;
                case 'H': k->key = FIRE_KEY_HOME; break;
                case 'F': k->key = FIRE_KEY_END; break;
                case 'P': case 'Q': case 'R': case 'S': k->key = FIRE_KEY_F1 + (e - 'P'); break;
                case '~':
                    switch (num) {
                        case 1: case 7: k->key = FIRE_KEY_HOME; break;
                        case 4: case 8: k->key = FIRE_KEY_END; break;
                        case 2: k->key = FIRE_KEY_INSERT; break;
                        case 3: k->key = FIRE_KEY_DELETE; break;
                        case 5: k->key = FIRE_KEY_PAGEUP; break;
                        case 6: k->key = FIRE_KEY_PAGEDOWN; break;
                        case 11: case 12: case 13: case 14: case 15: k->key = FIRE_KEY_F1 + (num - 11); break;
                        case 17: case 18: case 19: case 20: case 21: k->key = FIRE_KEY_F1 + 5 + (num - 17); break;
                        case 23: case 24: k->key = FIRE_KEY_F1 + 10 + (num - 23); break;
                        default: break;
                    }
                    break;
                default: break;
            }
        } else {   /* Alt and a key: ESC first */
            fire_con_keyOfChar((uint32_t)d, k);
            k->mods |= FIRE_KEYMOD_ALT;
        }
    } else {
        char text[4];
        uint32_t len = c < 0x80 ? 1 : (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : (c >> 3) == 30 ? 4 : 1, i, at = 0;
        text[0] = (char)c;
        for (i = 1; i < len; i++) { int b = fire_con_byte(50); if (b < 0) { len = i; break; } text[i] = (char)b; }
        fire_con_keyOfChar(fire_con_utf8Next(text, len, &at), k);
    }
    fire_con_raw(was);
    return 1;
}

#endif
