/* fire windows package: display part for DOS (dos16 in real mode, dos32 with a DOS extender - platform/std/fire_dos_bios.h; Open Watcom): a window is the whole screen in a graphics mode of the VGA -
 *   - Mode 13h (320x200, 256 colours) for a framebuffer up to 320x200, VESA mode 101h (640x480, 256 colours, banked) for a larger one when the BIOS has it
 *     (else 13h); the framebuffer is stretched to the screen (nearest pixel), the text mode comes back when the window closes or the program ends;
 *   - a palette framebuffer goes straight into the video memory, its palette into the registers of the card (FIRE_DISPLAY_PRESENT_FB: the windows bridge
 *     hands over the framebuffer, no copy as colours); an RGBA one through the 6x6x6 colour cube and 40 grays;
 *   - keys from the BIOS (INT 16h: the set-1 scan codes, translated as on Windows - fire_key_fromWin32; a key is pressed and released in the same cycle: the
 *     BIOS tells no releases), the mouse from its driver (INT 33h) when there is one; its cursor is hidden while a frame is written.
 * No desktop: the clipboard is the program's own, nothing can be dragged out. */
#ifndef FIRE_DISPLAY_DOS_H
#define FIRE_DISPLAY_DOS_H

#include "fire_display_c.h"
#include "fire_display_keys.h"
#include "platform/std/fire_cp437.h"   /* (the characters of the keys; a helper of the platform layer, the console package uses it too) */
#include "platform/std/fire_dos_bios.h"   /* (the BIOS and the first megabyte: real mode and DOS extender) */
#include <bios.h>
#include <conio.h>
#include <dos.h>
#include <i86.h>
#include <stdlib.h>

#define FIRE_DISPLAY_AVAILABLE 1
#define FIRE_DISPLAY_PRESENT_FB 1

typedef struct FireDisplayWindow {
    int vsync, touchMouse;
    int open;
    int mode;                 /* 0x13 or 0x101 */
    int sw, sh, pitch;        /* the screen */
    unsigned long granularity;   /* VESA: the step of the bank window in bytes */
    int bank;
    int mouse, buttons, mx, my;
    int palette;              /* what the registers hold: -1 nothing yet, -2 the colour cube, else the version of a framebuffer's palette */
    const void* paletteOf;
    unsigned char prevMode;
} FireDisplayWindow;

static void fire_display_systemTheme(FireSystemTheme* t) {
    fire_display_themeDefaults(t);
    t->embedded = 1;
    t->transparency = 0;
    t->scale = 1.0;
    t->dark = 1;
    t->accent = 0x8854D0;
}
static void fire_display_threadDone(void) {}

static void fire_display_dosBios(union REGS* r) { fire_dos_int(0x10, r); }
static int fire_display_dosMouse(int ax, int cx, int dx, int* bx, int* cxOut, int* dxOut) {
    union REGS r;
    r.FIRE_DOS_W.ax = (unsigned)ax; r.FIRE_DOS_W.bx = 0; r.FIRE_DOS_W.cx = (unsigned)cx; r.FIRE_DOS_W.dx = (unsigned)dx;
    fire_dos_int(0x33, &r);
    if (bx) *bx = (int)r.FIRE_DOS_W.bx;
    if (cxOut) *cxOut = (int)r.FIRE_DOS_W.cx;
    if (dxOut) *dxOut = (int)r.FIRE_DOS_W.dx;
    return (int)r.FIRE_DOS_W.ax;
}
/* (INT 33h: a vector of 0 or one that points at an IRET means no driver) */
static int fire_display_dosHasMouse(void) {
    unsigned off = *(unsigned short FIRE_DOS_FAR*)FIRE_DOS_LOW(0, 0x33 * 4), seg = *(unsigned short FIRE_DOS_FAR*)FIRE_DOS_LOW(0, 0x33 * 4 + 2);
    if ((seg | off) == 0 || *FIRE_DOS_LOW(seg, off) == 0xCF) return 0;
    return (unsigned)fire_display_dosMouse(0, 0, 0, NULL, NULL, NULL) == 0xFFFFu;
}

static void fire_display_init(FireDisplayWindow* w) { memset(w, 0, sizeof *w); w->vsync = 1; w->touchMouse = 1; w->palette = -1; }

/* the colour cube: 216 colours (6 steps of each) and 40 grays */
static void fire_display_dosCube(FireDisplayWindow* w) {
    int i;
    if (w->palette == -2) return;
    outp(0x3C8, 0);
    for (i = 0; i < 256; i++) {
        int r, g, b;
        if (i < 216) { r = (i / 36) * 63 / 5; g = (i / 6 % 6) * 63 / 5; b = (i % 6) * 63 / 5; }
        else r = g = b = (i - 216) * 63 / 39;
        outp(0x3C9, r); outp(0x3C9, g); outp(0x3C9, b);
    }
    w->palette = -2;
    w->paletteOf = NULL;
}
static unsigned char fire_display_dosCubeOf(uint32_t c) {
    unsigned r = (unsigned)(c & 0xFF), g = (unsigned)((c >> 8) & 0xFF), b = (unsigned)((c >> 16) & 0xFF);
    if (r == g && g == b && r % 51 != 0) return (unsigned char)(216 + (r * 39 + 127) / 255);   /* (a gray between the steps of the cube) */
    return (unsigned char)(((r * 5 + 127) / 255) * 36 + ((g * 5 + 127) / 255) * 6 + (b * 5 + 127) / 255);
}
/* the palette of a framebuffer into the registers (only when it changed) */
static void fire_display_dosPalette(FireDisplayWindow* w, const FireGfxFramebuffer* fb) {
    int i;
    if (w->paletteOf == (const void*)fb && w->palette == (int)fb->palette.version) return;
    outp(0x3C8, 0);
    for (i = 0; i < 256; i++) {
        uint32_t c = fb->palette.entries[i];
        outp(0x3C9, (int)((c & 0xFF) >> 2)); outp(0x3C9, (int)(((c >> 8) & 0xFF) >> 2)); outp(0x3C9, (int)(((c >> 16) & 0xFF) >> 2));
    }
    w->palette = (int)fb->palette.version;
    w->paletteOf = (const void*)fb;
}

static int fire_display_open(FireDisplayWindow* w, const char* title, int width, int height, int vsync) {
    union REGS r;
    (void)title;
    if (w->open) return 1;
    fflush(stdout);
    r.h.ah = 0x0F; fire_display_dosBios(&r); w->prevMode = r.h.al;
    w->vsync = vsync;
    w->mode = 0x13; w->sw = 320; w->sh = 200; w->pitch = 320; w->granularity = 65536ul; w->bank = -1;
    if (width > 320 || height > 200) {
        /* VESA 101h: the information of the mode (INT 10h AX=4F01h) - the window granularity and the bytes per line */
        unsigned seg = 0;
        unsigned char FIRE_DOS_FAR* info = fire_dos_lowBuffer(&seg);   /* (below 1 MB: the BIOS writes it in real mode) */
        r.FIRE_DOS_W.ax = 0x4F01; r.FIRE_DOS_W.cx = 0x101; r.FIRE_DOS_W.di = 0;
        if (info != NULL) fire_dos_realInt(0x10, &r, seg);
        if (info != NULL && r.FIRE_DOS_W.ax == 0x004F && (info[0] & 1)) {
            r.FIRE_DOS_W.ax = 0x4F02; r.FIRE_DOS_W.bx = 0x101;
            fire_display_dosBios(&r);
            if (r.FIRE_DOS_W.ax == 0x004F) {
                unsigned gran = info[4] | (info[5] << 8);
                w->mode = 0x101; w->sw = 640; w->sh = 480; w->pitch = info[16] | (info[17] << 8);
                w->granularity = (unsigned long)(gran ? gran : 64) * 1024ul;
            }
        }
    }
    if (w->mode == 0x13) { r.FIRE_DOS_W.ax = 0x0013; fire_display_dosBios(&r); }
    w->palette = -1;
    w->open = 1;
    w->mouse = fire_display_dosHasMouse();
    if (w->mouse) {
        fire_display_dosMouse(7, 0, w->sw - 1, NULL, NULL, NULL);
        fire_display_dosMouse(8, 0, w->sh - 1, NULL, NULL, NULL);
        fire_display_dosMouse(1, 0, 0, NULL, NULL, NULL);
        fire_display_dosMouse(3, 0, 0, &w->buttons, &w->mx, &w->my);
    }
    return 1;
}
static void fire_display_close(FireDisplayWindow* w) {
    union REGS r;
    if (!w->open) return;
    if (w->mouse) fire_display_dosMouse(2, 0, 0, NULL, NULL, NULL);
    r.h.ah = 0; r.h.al = w->prevMode ? w->prevMode : 3;
    fire_display_dosBios(&r);
    w->open = 0;
}
static void fire_display_setVSync(FireDisplayWindow* w, int v) { w->vsync = v; }
static void fire_display_setSize(FireDisplayWindow* w, int width, int height) { (void)w; (void)width; (void)height; }
static void fire_display_setCursor(FireDisplayWindow* w, int c) { (void)w; (void)c; }
static int fire_display_vsync(const FireDisplayWindow* w) { return w->vsync; }
static void fire_display_setTouchMouse(FireDisplayWindow* w, int v) { w->touchMouse = v; }
static int fire_display_touchMouse(const FireDisplayWindow* w) { return w->touchMouse; }
static int fire_display_native(FireDisplayWindow* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_display_restore(FireDisplayWindow* w) { (void)w; }

/* ---- writing a frame ---- */
static void fire_display_dosBank(FireDisplayWindow* w, int bank) {
    union REGS r;
    if (bank == w->bank) return;
    r.FIRE_DOS_W.ax = 0x4F05; r.FIRE_DOS_W.bx = 0; r.FIRE_DOS_W.dx = (unsigned)bank;
    fire_display_dosBios(&r);
    w->bank = bank;
}
/* one line of the screen (`n` bytes of `row` from column 0 of line y) */
static void fire_display_dosLine(FireDisplayWindow* w, int y, const unsigned char* row, int n) {
    unsigned long at = (unsigned long)y * (unsigned long)w->pitch;
    int done = 0;
    if (w->mode == 0x13) { FIRE_DOS_COPY(FIRE_DOS_LOW(0xA000, (unsigned)at), row, (size_t)n); return; }
    while (done < n) {
        unsigned long off = (at + (unsigned long)done) % w->granularity;
        unsigned long room = 65536ul - off;
        int step = (unsigned long)(n - done) < room ? n - done : (int)room;
        fire_display_dosBank(w, (int)((at + (unsigned long)done) / w->granularity));
        FIRE_DOS_COPY(FIRE_DOS_LOW(0xA000, (unsigned)off), row + done, (size_t)step);
        done += step;
    }
}
static void fire_display_dosRetrace(const FireDisplayWindow* w) {
    if (!w->vsync) return;
    while (inp(0x3DA) & 8) {}
    while (!(inp(0x3DA) & 8)) {}
}
/* the framebuffer stretched to the screen: each line of the screen from the nearest line and column of the framebuffer, as colour indices */
static void fire_display_dosFrame(FireDisplayWindow* w, const FireGfxFramebuffer* fb, const uint32_t* rgba) {
    static unsigned char line[640];
    static int columns[640];
    int x, y, fw = (int)fb->width, fh = (int)fb->height;
    if (!w->open || fw <= 0 || fh <= 0) return;
    for (x = 0; x < w->sw; x++) columns[x] = (int)((long)x * fw / w->sw);
    if (w->mouse) fire_display_dosMouse(2, 0, 0, NULL, NULL, NULL);
    fire_display_dosRetrace(w);
    for (y = 0; y < w->sh; y++) {
        uint32_t from = (uint32_t)((long)y * fh / w->sh) * (uint32_t)fw;
        if (fb->indices) { const uint8_t* src = fb->indices + from; for (x = 0; x < w->sw; x++) line[x] = src[columns[x]]; }
        else { const uint32_t* src = (rgba ? rgba : fb->pixels) + from; for (x = 0; x < w->sw; x++) line[x] = fire_display_dosCubeOf(src[columns[x]]); }
        fire_display_dosLine(w, y, line, w->sw);
    }
    if (w->mouse) fire_display_dosMouse(1, 0, 0, NULL, NULL, NULL);
}
static void fire_display_presentFramebuffer(FireDisplayWindow* w, const FireGfxFramebuffer* fb, const int* area) {
    (void)area;
    if (fb->indices) fire_display_dosPalette(w, fb); else fire_display_dosCube(w);
    fire_display_dosFrame(w, fb, NULL);
}
/* (colours from somewhere else - a presenter's frame: through the cube) */
static void fire_display_present(FireDisplayWindow* w, const uint32_t* rgba, int width, int height) {
    FireGfxFramebuffer view;
    memset(&view, 0, sizeof view);
    view.width = width; view.height = height;
    fire_display_dosCube(w);
    fire_display_dosFrame(w, &view, rgba);
}

/* ---- events ---- */
/* a code of the enhanced BIOS keyboard (Ctrl/Alt/Shift with the cursor block and F-keys, F11/F12) as the set-1 scan code of the key itself */
static unsigned fire_display_dosBaseScan(unsigned scan) {
    static const unsigned char ctrl[][2] = { {0x73, 0x4B}, {0x74, 0x4D}, {0x77, 0x47}, {0x75, 0x4F}, {0x84, 0x49}, {0x76, 0x51}, {0x8D, 0x48}, {0x91, 0x50},
        {0x92, 0x52}, {0x93, 0x53}, {0x97, 0x47}, {0x98, 0x48}, {0x99, 0x49}, {0x9B, 0x4B}, {0x9D, 0x4D}, {0x9F, 0x4F}, {0xA0, 0x50}, {0xA1, 0x51}, {0xA2, 0x52},
        {0xA3, 0x53}, {0x85, 0x57}, {0x86, 0x58}, {0x87, 0x57}, {0x88, 0x58}, {0x89, 0x57}, {0x8A, 0x58}, {0x8B, 0x57}, {0x8C, 0x58} };
    size_t i;
    if (scan >= 0x54 && scan <= 0x71) return 0x3B + (scan - 0x54) % 10;   /* F1..F10 with Shift, Ctrl, Alt */
    for (i = 0; i < sizeof ctrl / sizeof ctrl[0]; i++) if (ctrl[i][0] == scan) return ctrl[i][1];
    return scan;
}
static void fire_display_dosKeys(FireDisplayEvents* out) {
    while (_bios_keybrd(_NKEYBRD_READY)) {
        unsigned code = _bios_keybrd(_NKEYBRD_READ), shift = _bios_keybrd(_NKEYBRD_SHIFTSTATUS);
        unsigned scan = fire_display_dosBaseScan((code >> 8) & 0xFF), ascii = code & 0xFF;
        int extended = ascii == 0xE0 || (ascii == 0 && scan >= 0x47 && scan <= 0x53 && scan != 0x4A && scan != 0x4C && scan != 0x4E);
        int sc = fire_key_fromWin32(scan, extended);
        uint32_t ch = ascii != 0 && ascii != 0xE0 ? fire_cp437_char((unsigned char)ascii) : 0;
        int mods = ((shift & 1) ? 0x2 : 0) | ((shift & 2) ? 0x1 : 0) | ((shift & 4) ? 0x40 : 0) | ((shift & 8) ? 0x100 : 0) | ((shift & 0x20) ? 0x1000 : 0) | ((shift & 0x40) ? 0x2000 : 0);
        FireDisplayEvent e;
        uint32_t unshifted = ch >= 'A' && ch <= 'Z' ? ch + 32 : (ch >= 32 && ch < 127 ? ch : 0);
        fire_display_eventInit(&e);
        e.type = FIRE_EV_KEY_DOWN; e.scanCode = sc; e.keyCode = fire_key_code(sc, unshifted); e.modifier = mods;
        fire_display_push(out, &e);
        if (ch >= 32 && !(mods & (0x40 | 0x100))) {
            FireDisplayEvent t;
            fire_display_eventInit(&t);
            t.type = FIRE_EV_TEXT_INPUT;
            if (ch < 0x80) t.text[0] = (char)ch;
            else if (ch < 0x800) { t.text[0] = (char)(0xC0 | (ch >> 6)); t.text[1] = (char)(0x80 | (ch & 0x3F)); }
            else { t.text[0] = (char)(0xE0 | (ch >> 12)); t.text[1] = (char)(0x80 | ((ch >> 6) & 0x3F)); t.text[2] = (char)(0x80 | (ch & 0x3F)); }
            fire_display_push(out, &t);
        }
        e.type = FIRE_EV_KEY_UP;
        fire_display_push(out, &e);
    }
}
static void fire_display_dosMouseEvents(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    int buttons, x, y, i;
    if (!w->mouse) return;
    fire_display_dosMouse(3, 0, 0, &buttons, &x, &y);
    if (x != w->mx || y != w->my) {
        FireDisplayEvent e;
        fire_display_eventInit(&e);
        e.type = FIRE_EV_MOUSE_MOVE;
        e.x = (float)((long)x * fbWidth / w->sw); e.y = (float)((long)y * fbHeight / w->sh);
        e.xrel = (float)((long)(x - w->mx) * fbWidth / w->sw); e.yrel = (float)((long)(y - w->my) * fbHeight / w->sh);
        e.buttonState = (buttons & 1) | ((buttons & 4) ? 2 : 0) | ((buttons & 2) ? 4 : 0);
        fire_display_push(out, &e);
        w->mx = x; w->my = y;
    }
    for (i = 0; i < 3; i++) {
        int bit = 1 << i;
        if ((buttons & bit) != (w->buttons & bit)) {
            FireDisplayEvent e;
            fire_display_eventInit(&e);
            e.type = (buttons & bit) ? FIRE_EV_MOUSE_DOWN : FIRE_EV_MOUSE_UP;
            e.button = i == 0 ? 1 : i == 1 ? 3 : 2;   /* (the driver: bit 1 right, bit 2 middle) */
            e.x = (float)((long)x * fbWidth / w->sw); e.y = (float)((long)y * fbHeight / w->sh);
            fire_display_push(out, &e);
        }
    }
    w->buttons = buttons;
}
static int fire_display_pump(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    if (!w->open) return 0;
    fire_display_dosKeys(out);
    fire_display_dosMouseEvents(w, out, fbWidth, fbHeight);
    return 1;
}

/* No desktop: the clipboard is the program's own. */
static int fire_display_clipboardSet(const char* text) { return fire_display_memSetText(text); }
static char* fire_display_clipboardGet(void) { return fire_display_memGetText(); }
static int fire_display_clipboardHas(void) { return fire_display_memHasText(); }
static unsigned char* fire_display_clipboardGetImage(size_t* size) { return fire_display_memGetImage(size); }
static int fire_display_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) { (void)bmp; (void)bmpSize; return fire_display_memSetImage(png, pngSize); }
static int fire_display_clipboardHasImage(void) { return fire_display_memHasImage(); }
static char* fire_display_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_display_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_display_clipboardHasFiles(void) { return fire_display_memHasFiles(); }
#define FIRE_DISPLAY_START_DRAG 1
static int fire_display_startDrag(FireDisplayWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
#define FIRE_DISPLAY_CAN_START_DRAG 1
static int fire_display_canStartDrag(FireDisplayWindow* w) { (void)w; return 0; }

#endif
