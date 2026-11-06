/* fire native bridge "console" (C): the natives behind `#import "console"` (docs/stdlib/console.md) - the screen and the keyboard of a text console.
 *
 * The console itself comes with the package, one file per platform (fire_con_*; DOS through the BIOS, POSIX terminals with ANSI and termios, the console API
 * of Windows; chosen by the platformSources of console.fireproj). When the program has none - its output or input is redirected, the platform has no console, or it runs in the VM (the library
 * for the VM writes to the output of the host) - the console is "plain": text goes to standard output without colours, the cursor stays where the text puts
 * it, keys are the characters of standard input. A program behaves the same either way, only the screen looks different. */
#ifndef FIRE_BRIDGE_CONSOLE_H
#define FIRE_BRIDGE_CONSOLE_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "fire_console_c.h"   /* (and before this file the console of the platform: platformSources of console.fireproj - the VM gets fire_console_none.h) */

static int g_conState = -1;   /* -1: not asked yet, 0: plain, 1: a console */
static int fire_con_live(void) {
    if (g_conState < 0) g_conState = fire_con_open() ? 1 : 0;
    return g_conState;
}

/* ---- plain: standard output and input ---------------------------------------------------------------------------------------------------------------- */
#ifdef FIRE_LIBRARY
static uint8_t g_conIn[256];
static int g_conInPos = 0, g_conInLen = 0, g_conInEnd = 0;
static void fire_con_plainWrite(const char* s, uint32_t n) {
    const fire_host* host = libraryHost();
    if (host && host->std_write && n > 0) host->std_write(1, (const uint8_t*)s, (int32_t)n);
}
static void fire_con_plainFlush(void) { const fire_host* host = libraryHost(); if (host && host->std_flush) host->std_flush(1); }
static int fire_con_plainPeek(void) {
    if (g_conInPos >= g_conInLen && !g_conInEnd) {
        const fire_host* host = libraryHost();
        int n = host && host->std_read ? host->std_read(0, g_conIn, (int32_t)sizeof g_conIn) : 0;
        if (n <= 0) g_conInEnd = 1; else { g_conInPos = 0; g_conInLen = n; }
    }
    return g_conInPos < g_conInLen ? g_conIn[g_conInPos] : -1;
}
static int fire_con_plainGet(void) { int c = fire_con_plainPeek(); if (c >= 0) g_conInPos++; return c; }
#else
static void fire_con_plainWrite(const char* s, uint32_t n) { if (n > 0) fwrite(s, 1, n, stdout); }
static void fire_con_plainFlush(void) { fflush(stdout); }
static int fire_con_plainPeek(void) { int c; fflush(stdout); c = getc(stdin); if (c >= 0) ungetc(c, stdin); return c; }
static int fire_con_plainGet(void) { fflush(stdout); return getc(stdin); }
#endif
/* a character of standard input as a key (UTF-8; \r\n is one Enter); 0 at the end */
static int fire_con_plainKey(FireConKey* k) {
    char text[4];
    uint32_t len, i, at = 0;
    int c = fire_con_plainGet();
    if (c < 0) return 0;
    len = c < 0x80 ? 1 : (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : (c >> 3) == 30 ? 4 : 1;
    text[0] = (char)c;
    for (i = 1; i < len; i++) { int b = fire_con_plainGet(); if (b < 0) { len = i; break; } text[i] = (char)b; }
    if (c == '\r' && fire_con_plainPeek() == '\n') fire_con_plainGet();
    fire_con_keyOfChar(fire_con_utf8Next(text, len, &at), k);
    return 1;
}

/* the text of a string in UTF-8 (malloc; NULL: out of memory) */
static char* fire_con_utf8(Value v, uint32_t* length) {
    const Str* s;
    char* out;
    uint32_t i, n = 0;
    *length = 0;
    if (v.kind != K_String) return NULL;
    s = strOf(v);
    out = (char*)malloc((size_t)s->length * 3 + 1);
    if (out == NULL) return NULL;
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < s->length && s->data[i + 1] >= 0xDC00 && s->data[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (c < 0x80) out[n++] = (char)c;
        else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[n] = 0;
    *length = n;
    return out;
}

/* ---- the natives (called as fire_console_Name; the fire name is __ConName) --------------------------------------------------------------------------- */
static Value fire_console_IsConsole(void) { return Bool(fire_con_live() == 1); }
static Value fire_console_Write(Value text) {
    uint32_t n;
    char* s = fire_con_utf8(text, &n);
    if (s == NULL) return Undef();
    if (fire_con_live()) fire_con_write(s, n); else fire_con_plainWrite(s, n);
    free(s);
    return Undef();
}
static Value fire_console_Flush(void) { if (fire_con_live()) fire_con_flush(); else fire_con_plainFlush(); return Undef(); }
static Value fire_console_Width(void) { int c = 80, r = 25; if (fire_con_live()) fire_con_size(&c, &r); return Int(c); }
static Value fire_console_Height(void) { int c = 80, r = 25; if (fire_con_live()) fire_con_size(&c, &r); return Int(r); }
static Value fire_console_Column(void) { int c = 0, r = 0; if (fire_con_live()) { fire_con_flush(); fire_con_getCursor(&c, &r); } return Int(c); }
static Value fire_console_Row(void) { int c = 0, r = 0; if (fire_con_live()) { fire_con_flush(); fire_con_getCursor(&c, &r); } return Int(r); }
static Value fire_console_SetCursor(Value col, Value row) { if (fire_con_live()) fire_con_setCursor((int)col.i, (int)row.i); return Undef(); }
static Value fire_console_SetColor(Value fg, Value bg) {
    if (fire_con_live()) fire_con_setColor(fg.kind == K_Int ? (int)fg.i : -1, bg.kind == K_Int ? (int)bg.i : -1);
    return Undef();
}
static Value fire_console_Clear(void) { if (fire_con_live()) fire_con_clear(); return Undef(); }
static Value fire_console_ShowCursor(Value show) { if (fire_con_live()) fire_con_showCursor(show.kind == K_Bool ? show.i != 0 : 1); return Undef(); }
static Value fire_console_KeyAvailable(void) {
    if (fire_con_live()) { fire_con_flush(); return Bool(fire_con_keyAvailable() != 0); }
    fire_con_plainFlush();
    return Bool(fire_con_plainPeek() >= 0);
}
/* the next key: key | mods << 5 | character << 8 (Console.KeyInfo takes it apart); -1 when no more keys come */
static Value fire_console_ReadKey(void) {
    FireConKey k;
    int got;
    k.key = FIRE_KEY_NONE; k.ch = 0; k.mods = 0;
    if (fire_con_live()) {
        fire_con_flush();
#if defined(FIRE_THREADS) && !defined(FIRE_LIBRARY)
        while (!fire_con_keyAvailable()) {   /* (waits outside the lock: other fire threads go on) */
            gilRelease();
            fire_plat_sleepMs(10);
            gilAcquire();
        }
#endif
        got = fire_con_readKey(&k);
    } else {
        fire_con_plainFlush();
        got = fire_con_plainKey(&k);
    }
    if (!got) return Int(-1);
    return Int((int64_t)k.key | ((int64_t)k.mods << 5) | ((int64_t)k.ch << 8));
}

#endif
