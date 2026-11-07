/* fire windows package: display part for Windows 3.x (win16; Open Watcom): a window of its own through GDI -
 *   - the framebuffer stretched into the client area with StretchDIBits, in bands below 64 KB (a palette framebuffer as an 8-bit DIB with its palette - FIRE_DISPLAY_PRESENT_FB:
 *     the bridge hands over the framebuffer -, an RGBA one as a 24-bit DIB);
 *   - keys from WM_KEYDOWN/WM_KEYUP (the scan code of lParam, as on Win32 - fire_key_fromWin32), text from WM_CHAR (the ANSI code page: Latin-1), the mouse in
 *     framebuffer pixels, the size of the window, closing as a request;
 *   - the mouse pointer of the program (WM_SETCURSOR; what Windows 3.1 does not have - the hand, "not allowed" - is the arrow), files dropped from the File Manager
 *     (DragAcceptFiles of SHELL.DLL: FIRE_EV_DROP_FILES), the colours of the desktop (GetSysColor) for the system theme;
 *   - the clipboard of Windows: text (CF_TEXT, the ANSI code page taken as Latin-1, lines with CR LF), pictures (CF_DIB - the BMP of the bridge without its file
 *     header; back as a BMP file); paths stay the program's own (Windows 3.x has no format for them);
 *   - the messages are taken when the program pumps (Tick, Sleep of the platform): Windows 3.x waits for every program to take them.
 * Nothing can be dragged out of a window. */
#ifndef FIRE_DISPLAY_WIN16_H
#define FIRE_DISPLAY_WIN16_H

#include "fire_display_c.h"
#include "fire_display_keys.h"
#include <windows.h>
#include <shellapi.h>
#include <stdlib.h>
#include <string.h>

#define FIRE_DISPLAY_AVAILABLE 1
#define FIRE_DISPLAY_PRESENT_FB 1

typedef struct FireDisplayWindow {
    int vsync, touchMouse;
    HWND hwnd;
    int open, closed;
    int cw, ch;                  /* the client area */
    int fbw, fbh;                /* the framebuffer of the last frame (for the positions of the mouse) */
    int buttons, mx, my;
    int cursor;                  /* FIRE_CURSOR_* */
    FireDisplayEvents pending;   /* what the window procedure collected since the last pump */
} FireDisplayWindow;

static void fire_display_systemTheme(FireSystemTheme* t) {
    DWORD caption = GetSysColor(COLOR_ACTIVECAPTION), window = GetSysColor(COLOR_WINDOW);   /* (0x00BBGGRR) */
    unsigned brightness = (unsigned)(GetRValue(window) * 3u + GetGValue(window) * 6u + GetBValue(window)) / 10u;
    fire_display_themeDefaults(t);
    t->embedded = 0;
    t->transparency = 0;
    t->scale = 1.0;
    t->dark = brightness < 96;
    t->accent = ((int)GetRValue(caption) << 16) | ((int)GetGValue(caption) << 8) | (int)GetBValue(caption);
    strcpy(t->fonts, "MS Sans Serif;Arial;System");
    t->fontSize = 13;
}
static void fire_display_threadDone(void) {}

/* the windows that are open (the window procedure finds its window by the handle) */
#define FIRE_W16_MAX 8
static FireDisplayWindow* g_w16Windows[FIRE_W16_MAX];
static FireDisplayWindow* fire_w16_of(HWND h) {
    int i;
    for (i = 0; i < FIRE_W16_MAX; i++) if (g_w16Windows[i] && g_w16Windows[i]->hwnd == h) return g_w16Windows[i];
    return NULL;
}
static void fire_w16_mouse(FireDisplayWindow* w, int type, int button, LPARAM lp) {
    FireDisplayEvent e;
    int x = (int)(short)LOWORD(lp), y = (int)(short)HIWORD(lp);
    int fw = w->fbw > 0 ? w->fbw : w->cw, fh = w->fbh > 0 ? w->fbh : w->ch;
    fire_display_eventInit(&e);
    e.type = type;
    e.button = button;
    e.x = (float)((long)x * fw / (w->cw > 0 ? w->cw : 1));
    e.y = (float)((long)y * fh / (w->ch > 0 ? w->ch : 1));
    if (type == FIRE_EV_MOUSE_MOVE) {
        e.xrel = (float)((long)(x - w->mx) * fw / (w->cw > 0 ? w->cw : 1));
        e.yrel = (float)((long)(y - w->my) * fh / (w->ch > 0 ? w->ch : 1));
        e.buttonState = w->buttons;
        w->mx = x; w->my = y;
    }
    fire_display_push(&w->pending, &e);
}
static int fire_w16_mods(void) {
    return ((GetKeyState(VK_SHIFT) & 0x8000) ? 0x1 : 0) | ((GetKeyState(VK_CONTROL) & 0x8000) ? 0x40 : 0) | ((GetKeyState(VK_MENU) & 0x8000) ? 0x100 : 0);
}
LRESULT CALLBACK __export fire_w16_proc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
    FireDisplayWindow* w = fire_w16_of(h);
    FireDisplayEvent e;
    if (w == NULL) return DefWindowProc(h, msg, wp, lp);
    switch (msg) {
    case WM_CLOSE:
        fire_display_eventInit(&e);
        e.type = FIRE_EV_CLOSE_REQUEST;
        fire_display_push(&w->pending, &e);
        return 0;
    case WM_DESTROY:
        w->closed = 1;
        return 0;
    case WM_SIZE:
        w->cw = LOWORD(lp); w->ch = HIWORD(lp);
        fire_display_eventInit(&e);
        e.type = FIRE_EV_RESIZE; e.width = w->cw; e.height = w->ch;
        fire_display_push(&w->pending, &e);
        return 0;
    case WM_KEYDOWN: case WM_KEYUP: case WM_SYSKEYDOWN: case WM_SYSKEYUP: {
        int scan = (int)((lp >> 16) & 0xFF), extended = (int)((lp >> 24) & 1);
        int sc = fire_key_fromWin32((unsigned)scan, extended);
        unsigned ch = (wp >= 'A' && wp <= 'Z') ? (unsigned)wp + 32 : (wp >= '0' && wp <= '9') || wp == ' ' ? (unsigned)wp : 0;
        fire_display_eventInit(&e);
        e.type = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) ? FIRE_EV_KEY_DOWN : FIRE_EV_KEY_UP;
        e.scanCode = sc; e.keyCode = fire_key_code(sc, ch); e.modifier = fire_w16_mods();
        e.repeat = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && (lp & 0x40000000L) ? 1 : 0;
        fire_display_push(&w->pending, &e);
        if (msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP) break;
        return 0;
    }
    case WM_CHAR: {
        unsigned c = (unsigned)(wp & 0xFF);   /* (the ANSI code page of Windows 3.x: taken as Latin-1) */
        if (c < 32) return 0;
        fire_display_eventInit(&e);
        e.type = FIRE_EV_TEXT_INPUT;
        if (c < 0x80) e.text[0] = (char)c;
        else { e.text[0] = (char)(0xC0 | (c >> 6)); e.text[1] = (char)(0x80 | (c & 0x3F)); }
        fire_display_push(&w->pending, &e);
        return 0;
    }
    case WM_MOUSEMOVE: fire_w16_mouse(w, FIRE_EV_MOUSE_MOVE, 0, lp); return 0;
    case WM_LBUTTONDOWN: w->buttons |= 1; SetCapture(h); fire_w16_mouse(w, FIRE_EV_MOUSE_DOWN, 1, lp); return 0;
    case WM_LBUTTONUP: w->buttons &= ~1; ReleaseCapture(); fire_w16_mouse(w, FIRE_EV_MOUSE_UP, 1, lp); return 0;
    case WM_RBUTTONDOWN: w->buttons |= 4; fire_w16_mouse(w, FIRE_EV_MOUSE_DOWN, 3, lp); return 0;
    case WM_RBUTTONUP: w->buttons &= ~4; fire_w16_mouse(w, FIRE_EV_MOUSE_UP, 3, lp); return 0;
    case WM_MBUTTONDOWN: w->buttons |= 2; fire_w16_mouse(w, FIRE_EV_MOUSE_DOWN, 2, lp); return 0;
    case WM_MBUTTONUP: w->buttons &= ~2; fire_w16_mouse(w, FIRE_EV_MOUSE_UP, 2, lp); return 0;
    case WM_SETFOCUS: fire_display_eventInit(&e); e.type = FIRE_EV_FOCUS_GAINED; fire_display_push(&w->pending, &e); return 0;
    case WM_KILLFOCUS: fire_display_eventInit(&e); e.type = FIRE_EV_FOCUS_LOST; fire_display_push(&w->pending, &e); return 0;
    case WM_ERASEBKGND: return 1;   /* (the next frame paints it all) */
    case WM_SETCURSOR:
        if (LOWORD(lp) == HTCLIENT) {
            /* IDC_ARROW, IBEAM, WAIT, CROSS, (APPSTARTING: WAIT), SIZENWSE, SIZENESW, SIZEWE, SIZENS, (SIZEALL: IDC_SIZE), (NO, HAND: the arrow) */
            static const WORD ids[FIRE_CURSOR_COUNT] = { 32512, 32513, 32514, 32515, 32514, 32642, 32643, 32644, 32645, 32640, 32512, 32512 };
            SetCursor(LoadCursor(0, MAKEINTRESOURCE(ids[w->cursor >= 0 && w->cursor < FIRE_CURSOR_COUNT ? w->cursor : 0])));
            return TRUE;
        }
        break;
    case WM_DROPFILES: {
        HDROP drop = (HDROP)wp;
        UINT n = DragQueryFile(drop, (UINT)-1, NULL, 0), k;
        POINT at;
        size_t used = 0;
        char* all = (char*)malloc(n * 160u + 1);
        if (all != NULL) {
            for (k = 0; k < n; k++) {
                char name[160];
                UINT len = DragQueryFile(drop, k, (LPSTR)name, sizeof name);
                if (k > 0) all[used++] = '\n';
                memcpy(all + used, name, len);
                used += len;
            }
            all[used] = 0;
            DragQueryPoint(drop, &at);
            fire_display_eventInit(&e);
            e.type = FIRE_EV_DROP_FILES;
            e.data = all;
            e.x = (float)((long)at.x * (w->fbw > 0 ? w->fbw : w->cw) / (w->cw > 0 ? w->cw : 1));
            e.y = (float)((long)at.y * (w->fbh > 0 ? w->fbh : w->ch) / (w->ch > 0 ? w->ch : 1));
            fire_display_push(&w->pending, &e);
            free(all);
        }
        DragFinish(drop);
        return 0;
    }
    }
    return DefWindowProc(h, msg, wp, lp);
}

static void fire_display_init(FireDisplayWindow* w) { memset(w, 0, sizeof *w); w->vsync = 1; w->touchMouse = 1; }

static int fire_display_open(FireDisplayWindow* w, const char* title, int width, int height, int vsync) {
    static int registered = 0;
    RECT r;
    int i;
    if (w->open) return 1;
    if (!registered) {
        WNDCLASS c;
        memset(&c, 0, sizeof c);
        c.style = CS_HREDRAW | CS_VREDRAW;
        c.lpfnWndProc = fire_w16_proc;
        c.hInstance = g_win16Instance;
        c.hCursor = LoadCursor(NULL, IDC_ARROW);
        c.hIcon = LoadIcon(NULL, IDI_APPLICATION);
        c.lpszClassName = (LPCSTR)"FireWindow16";   /* (the huge model: a pointer is __huge, Windows takes __far) */
        if (!RegisterClass(&c)) return 0;
        registered = 1;
    }
    for (i = 0; i < FIRE_W16_MAX && g_w16Windows[i] != NULL; i++) {}
    if (i == FIRE_W16_MAX) return 0;
    g_w16Windows[i] = w;
    w->vsync = vsync;
    w->cw = width; w->ch = height;
    w->fbw = width; w->fbh = height;
    r.left = 0; r.top = 0; r.right = width; r.bottom = height;
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
    w->hwnd = CreateWindow((LPCSTR)"FireWindow16", (LPCSTR)title, WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, r.right - r.left, r.bottom - r.top,
                           (HWND)0, (HMENU)0, g_win16Instance, (void FAR*)0);   /* (no NULL: in the huge model it is a __huge pointer) */
    if (w->hwnd == 0) { g_w16Windows[i] = NULL; return 0; }   /* (a handle: 0, not NULL - a __huge pointer in this model) */
    DragAcceptFiles(w->hwnd, TRUE);   /* (files from the File Manager) */
    ShowWindow(w->hwnd, SW_SHOWNORMAL);
    UpdateWindow(w->hwnd);
    w->open = 1;
    return 1;
}
static void fire_display_close(FireDisplayWindow* w) {
    int i;
    if (!w->open) return;
    if (!w->closed) DestroyWindow(w->hwnd);
    for (i = 0; i < FIRE_W16_MAX; i++) if (g_w16Windows[i] == w) g_w16Windows[i] = NULL;
    fire_display_eventsFree(&w->pending);
    w->open = 0;
}
static void fire_display_setVSync(FireDisplayWindow* w, int v) { w->vsync = v; }
static void fire_display_setSize(FireDisplayWindow* w, int width, int height) {
    RECT r;
    if (!w->open) return;
    r.left = 0; r.top = 0; r.right = width; r.bottom = height;
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
    SetWindowPos(w->hwnd, NULL, 0, 0, r.right - r.left, r.bottom - r.top, SWP_NOMOVE | SWP_NOZORDER);
}
static void fire_display_setCursor(FireDisplayWindow* w, int c) {
    POINT p;
    RECT r;
    if (c < 0 || c >= FIRE_CURSOR_COUNT) return;
    w->cursor = c;
    if (!w->open) return;
    GetCursorPos(&p);
    GetWindowRect(w->hwnd, &r);
    if (PtInRect(&r, p)) SendMessage(w->hwnd, WM_SETCURSOR, (WPARAM)w->hwnd, MAKELPARAM(HTCLIENT, WM_MOUSEMOVE));   /* (at once, not with the next move) */
}
static int fire_display_vsync(const FireDisplayWindow* w) { return w->vsync; }
static void fire_display_setTouchMouse(FireDisplayWindow* w, int v) { w->touchMouse = v; }
static int fire_display_touchMouse(const FireDisplayWindow* w) { return w->touchMouse; }
static int fire_display_native(FireDisplayWindow* w, fire_native_window* out) { (void)w; (void)out; return 0; }
static void fire_display_restore(FireDisplayWindow* w) { (void)w; }

/* ---- writing a frame: bottom-up DIBs of a band of lines, each below 32 KB ---- */
#define FIRE_W16_BAND 32000u
static void fire_w16_frame(FireDisplayWindow* w, const FireGfxFramebuffer* fb, const uint32_t* rgba) {
    static struct { BITMAPINFOHEADER h; RGBQUAD colors[256]; } bmi;
    static unsigned char* band = NULL;
    int fw = (int)fb->width, fh = (int)fb->height, y0, i;
    int indexed = fb->indices != NULL && rgba == NULL;
    unsigned long stride;
    int rows;
    HDC dc;
    if (!w->open || w->closed || fw <= 0 || fh <= 0) return;
    if (band == NULL && (band = (unsigned char*)malloc(FIRE_W16_BAND)) == NULL) return;
    w->fbw = fw; w->fbh = fh;
    stride = indexed ? ((unsigned long)fw + 3u) & ~3ul : ((unsigned long)fw * 3u + 3u) & ~3ul;
    rows = (int)(FIRE_W16_BAND / stride);
    if (rows < 1) return;   /* (a line wider than a band: not shown) */
    memset(&bmi.h, 0, sizeof bmi.h);
    bmi.h.biSize = sizeof bmi.h;
    bmi.h.biWidth = fw;
    bmi.h.biPlanes = 1;
    bmi.h.biBitCount = (WORD)(indexed ? 8 : 24);
    bmi.h.biCompression = BI_RGB;
    if (indexed) {
        for (i = 0; i < 256; i++) {
            uint32_t c = fb->palette.entries[i];
            bmi.colors[i].rgbRed = (BYTE)(c & 0xFF); bmi.colors[i].rgbGreen = (BYTE)((c >> 8) & 0xFF); bmi.colors[i].rgbBlue = (BYTE)((c >> 16) & 0xFF);
            bmi.colors[i].rgbReserved = 0;
        }
        bmi.h.biClrUsed = 256;
    }
    dc = GetDC(w->hwnd);
    SetStretchBltMode(dc, COLORONCOLOR);
    for (y0 = 0; y0 < fh; y0 += rows) {
        int n = fh - y0 < rows ? fh - y0 : rows, r;
        int dy0 = (int)((long)y0 * w->ch / fh), dy1 = (int)((long)(y0 + n) * w->ch / fh);
        for (r = 0; r < n; r++) {
            unsigned char* dst = band + (unsigned long)(n - 1 - r) * stride;   /* (bottom-up) */
            int x;
            if (indexed) {
                const uint8_t* src = fb->indices + (unsigned long)(y0 + r) * (unsigned long)fw;
                for (x = 0; x < fw; x++) dst[x] = src[x];
            } else {
                const uint32_t* src = (rgba ? rgba : fb->pixels) + (unsigned long)(y0 + r) * (unsigned long)fw;
                for (x = 0; x < fw; x++) { uint32_t c = src[x]; dst[3 * x] = (unsigned char)(c >> 16); dst[3 * x + 1] = (unsigned char)(c >> 8); dst[3 * x + 2] = (unsigned char)c; }
            }
        }
        bmi.h.biHeight = n;
        if (dy1 > dy0) StretchDIBits(dc, 0, dy0, w->cw, dy1 - dy0, 0, 0, fw, n, (const void FAR*)band, (BITMAPINFO FAR*)&bmi, DIB_RGB_COLORS, SRCCOPY);
    }
    ReleaseDC(w->hwnd, dc);
    ValidateRect(w->hwnd, NULL);
}
static void fire_display_presentFramebuffer(FireDisplayWindow* w, const FireGfxFramebuffer* fb, const int* area) {
    (void)area;
    fire_w16_frame(w, fb, NULL);
}
static void fire_display_present(FireDisplayWindow* w, const uint32_t* rgba, int width, int height) {
    FireGfxFramebuffer view;
    memset(&view, 0, sizeof view);
    view.width = width; view.height = height;
    fire_w16_frame(w, &view, rgba);
}

/* ---- events: the messages of the program, then what the window collected ---- */
static int fire_display_pump(FireDisplayWindow* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    size_t i;
    if (!w->open) return 0;
    if (fbWidth > 0) w->fbw = fbWidth;
    if (fbHeight > 0) w->fbh = fbHeight;
    fire_win16_pump();
    for (i = 0; i < w->pending.count; i++) fire_display_push(out, &w->pending.items[i]);
    fire_display_eventsClear(&w->pending);
    return !w->closed;
}

/* ---- the clipboard of Windows: text (CF_TEXT) and pictures (CF_DIB); paths are the program's own ---- */
/* the owner of the clipboard: a window of the program (Windows 3.x wants one for OpenClipboard; NULL works too) */
static HWND fire_w16_clipOwner(void) { int i; for (i = 0; i < FIRE_W16_MAX; i++) if (g_w16Windows[i] && g_w16Windows[i]->open) return g_w16Windows[i]->hwnd; return 0; }
/* a block of global memory with the bytes (hmemcpy: also above 64 KB) */
static HGLOBAL fire_w16_global(const void* data, DWORD n, const void* head, DWORD headN) {
    HGLOBAL h = GlobalAlloc(GMEM_MOVEABLE | GMEM_DDESHARE, headN + n + 1);
    char __huge* p;
    if (h == 0) return 0;
    p = (char __huge*)GlobalLock(h);
    if (p == NULL) { GlobalFree(h); return 0; }
    if (headN > 0) hmemcpy(p, head, (long)headN);
    if (n > 0) hmemcpy(p + headN, data, (long)n);
    p[headN + n] = 0;
    GlobalUnlock(h);
    return h;
}
static int fire_display_clipboardSet(const char* text) {
    size_t n = strlen(text), i, o = 0;
    char* ansi = (char*)malloc(n * 2 + 1);
    HGLOBAL h;
    int ok = 0;
    if (ansi == NULL) return 0;
    for (i = 0; i < n; ) {   /* UTF-8 -> Latin-1 ('?' beyond it), LF -> CR LF */
        unsigned c = (unsigned char)text[i];
        unsigned long u = c;
        int len = c < 0x80 ? 1 : (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : 4;
        if (len == 2 && i + 1 < n) u = ((c & 0x1Fu) << 6) | ((unsigned char)text[i + 1] & 0x3Fu);
        else if (len > 2) u = 0xFFFF;
        i += (size_t)len;
        if (u == '\n') ansi[o++] = '\r';
        ansi[o++] = (char)(u < 256 ? u : '?');
    }
    h = fire_w16_global(ansi, (DWORD)o, NULL, 0);
    free(ansi);
    if (h == 0) return 0;
    if (OpenClipboard(fire_w16_clipOwner())) {
        EmptyClipboard();
        ok = SetClipboardData(CF_TEXT, h) != 0;
        CloseClipboard();
    }
    if (!ok) GlobalFree(h);
    fire_display_memSetText(text);   /* (the program's own copy too: a desktop without a clipboard owner) */
    return ok;
}
static char* fire_display_clipboardGet(void) {
    HGLOBAL h;
    const char __far* p;
    char* out = NULL;
    if (!IsClipboardFormatAvailable(CF_TEXT) || !OpenClipboard(fire_w16_clipOwner())) return NULL;
    h = GetClipboardData(CF_TEXT);
    if (h != 0 && (p = (const char __far*)GlobalLock(h)) != NULL) {
        size_t n = 0, i, o = 0;
        while (p[n] && n < 60000u) n++;
        out = (char*)malloc(n * 2 + 1);
        if (out != NULL) {
            for (i = 0; i < n; i++) {   /* Latin-1 -> UTF-8, CR LF -> LF */
                unsigned c = (unsigned char)p[i];
                if (c == '\r' && p[i + 1] == '\n') continue;
                if (c < 0x80) out[o++] = (char)c;
                else { out[o++] = (char)(0xC0 | (c >> 6)); out[o++] = (char)(0x80 | (c & 0x3F)); }
            }
            out[o] = 0;
        }
        GlobalUnlock(h);
    }
    CloseClipboard();
    return out;
}
static int fire_display_clipboardHas(void) { return IsClipboardFormatAvailable(CF_TEXT) != 0; }
/* a picture: the BMP of the bridge as CF_DIB (its file header of 14 bytes left out) */
static int fire_display_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) {
    HGLOBAL h;
    int ok = 0;
    fire_display_memSetImage(png, pngSize);
    if (bmp == NULL || bmpSize <= 14) return 0;
    h = fire_w16_global(bmp + 14, (DWORD)(bmpSize - 14), NULL, 0);
    if (h == 0) return 0;
    if (OpenClipboard(fire_w16_clipOwner())) {
        EmptyClipboard();
        ok = SetClipboardData(CF_DIB, h) != 0;
        CloseClipboard();
    }
    if (!ok) GlobalFree(h);
    return ok;
}
/* CF_DIB as the bytes of a BMP file (the header before it) */
static unsigned char* fire_display_clipboardGetImage(size_t* size) {
    HGLOBAL h;
    const unsigned char __far* p;
    unsigned char* out = NULL;
    *size = 0;
    if (!IsClipboardFormatAvailable(CF_DIB)) return fire_display_memGetImage(size);
    if (!OpenClipboard(fire_w16_clipOwner())) return NULL;
    h = GetClipboardData(CF_DIB);
    if (h != 0 && (p = (const unsigned char __far*)GlobalLock(h)) != NULL) {
        DWORD n = GlobalSize(h);
        const BITMAPINFOHEADER __far* bi = (const BITMAPINFOHEADER __far*)p;
        DWORD colors = bi->biBitCount <= 8 ? (bi->biClrUsed ? bi->biClrUsed : (1ul << bi->biBitCount)) : 0;
        DWORD offset = 14 + bi->biSize + colors * 4;
        out = (unsigned char*)malloc((size_t)(n + 14));
        if (out != NULL) {
            out[0] = 'B'; out[1] = 'M';
            out[2] = (unsigned char)(n + 14); out[3] = (unsigned char)((n + 14) >> 8); out[4] = (unsigned char)((n + 14) >> 16); out[5] = (unsigned char)((n + 14) >> 24);
            out[6] = out[7] = out[8] = out[9] = 0;
            out[10] = (unsigned char)offset; out[11] = (unsigned char)(offset >> 8); out[12] = (unsigned char)(offset >> 16); out[13] = (unsigned char)(offset >> 24);
            hmemcpy(out + 14, p, (long)n);
            *size = (size_t)(n + 14);
        }
        GlobalUnlock(h);
    }
    CloseClipboard();
    return out;
}
static int fire_display_clipboardHasImage(void) { return IsClipboardFormatAvailable(CF_DIB) != 0 || fire_display_memHasImage(); }
static char* fire_display_clipboardGetFiles(void) { return fire_display_memGetFiles(); }
static int fire_display_clipboardSetFiles(const char* paths) { return fire_display_memSetFiles(paths); }
static int fire_display_clipboardHasFiles(void) { return fire_display_memHasFiles(); }
#define FIRE_DISPLAY_START_DRAG 1
static int fire_display_startDrag(FireDisplayWindow* w, const char* text, const char* files) { (void)w; (void)text; (void)files; return 0; }
#define FIRE_DISPLAY_CAN_START_DRAG 1
static int fire_display_canStartDrag(FireDisplayWindow* w) { (void)w; return 0; }

#endif
