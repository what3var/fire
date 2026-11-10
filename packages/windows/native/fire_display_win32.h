/* fire native platform package "std": a window on Windows (fire_dw32_*, one of the displays of fire_display_desktop.h). It links user32 and gdi32 (what
 * every Windows has); the input method (imm32), the compositor (dwmapi), OLE (ole32: what other programs drop) and the version of Windows (ntdll) are loaded when
 * they are needed - a toolchain without their headers or import libraries (TinyCC) builds it all the same.
 *
 * A window belongs to the thread that opened it (the messages of a thread are pumped for all its windows; each window collects its own). It is DPI aware (per
 * monitor, v2): sizes and positions are pixels of the screen; a move to a screen with another scale reports FIRE_EV_SCALE and keeps the size Windows suggests.
 * What the window shows is the framebuffer, stretched to the client area with StretchDIBits (SetDIBitsToDevice when the sizes are the same). VSync: DwmFlush
 * after the frame (the compositor's beat), else a frame waits until 1/60 s since the last one passed.
 * Input: keys (the scan code from the message, the key code from the keyboard layout: fire_display_keys.h), text from WM_CHAR, compositions of the input method
 * (IMM: WM_IME_COMPOSITION - drawn by the program, the candidates at the spot it names), the mouse, the wheels, fingers (WM_POINTER on Windows 8 and later; mouse
 * messages Windows makes of a finger only with TouchMouse), what other programs drop (OLE's IDropTarget: files and text, with the position while it is dragged).
 * Behind the window (fire_display_setBackdrop): per-pixel alpha (DWM's blur-behind with an empty region), blur (the accent policy of Windows 10), acrylic (Windows
 * 11 22H2: the system backdrop, else the acrylic accent of Windows 10) and Mica (Windows 11). The title bar follows the dark mode of the system.
 * Where DirectComposition is there (fire_display_dcomp.h: Windows 8 and later), every window shows its frames through it instead of GDI - made with
 * WS_EX_NOREDIRECTIONBITMAP, a swap chain that DWM mixes by the premultiplied alpha (then that is the per-pixel transparency; blur-behind is not needed) - and a GPU
 * presenter gets it as FIRE_NATIVE_DCOMP. If DirectComposition fails for a window, it is made anew without (GDI). */
#ifndef FIRE_DISPLAY_WIN32_H
#define FIRE_DISPLAY_WIN32_H

#ifndef WIN32_LEAN_AND_MEAN
#  define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "fire_display_common.h"
#include "fire_dragout.h"
#include "fire_display_dcomp.h"

/* ---- what is loaded at run time ---- */
typedef struct FireW32CompForm { DWORD dwStyle; POINT ptCurrentPos; RECT rcArea; } FireW32CompForm;       /* COMPOSITIONFORM */
typedef struct FireW32CandForm { DWORD dwIndex; DWORD dwStyle; POINT ptCurrentPos; RECT rcArea; } FireW32CandForm;   /* CANDIDATEFORM */
typedef struct FireW32Margins { int left, right, top, bottom; } FireW32Margins;
typedef struct FireW32BlurBehind { DWORD dwFlags; BOOL fEnable; HRGN hRgnBlur; BOOL fTransitionOnMaximized; } FireW32BlurBehind;
typedef struct FireW32Accent { int state; int flags; DWORD gradient; int animation; } FireW32Accent;
typedef struct FireW32CompAttrib { int attrib; void* data; SIZE_T size; } FireW32CompAttrib;
typedef struct FireW32OsVersion { ULONG size, major, minor, build, platform; WCHAR csd[128]; } FireW32OsVersion;
typedef struct FireW32 {
    int tried;
    HMODULE imm, dwm, ole, ntdll, user;
    HANDLE (WINAPI *ImmGetContext)(HWND);
    BOOL (WINAPI *ImmReleaseContext)(HWND, HANDLE);
    LONG (WINAPI *ImmGetCompositionStringW)(HANDLE, DWORD, void*, DWORD);
    BOOL (WINAPI *ImmSetCompositionWindow)(HANDLE, FireW32CompForm*);
    BOOL (WINAPI *ImmSetCandidateWindow)(HANDLE, FireW32CandForm*);
    BOOL (WINAPI *ImmNotifyIME)(HANDLE, DWORD, DWORD, DWORD);
    HRESULT (WINAPI *DwmFlush)(void);
    HRESULT (WINAPI *DwmSetWindowAttribute)(HWND, DWORD, const void*, DWORD);
    HRESULT (WINAPI *DwmExtendFrameIntoClientArea)(HWND, const FireW32Margins*);
    HRESULT (WINAPI *DwmEnableBlurBehindWindow)(HWND, const FireW32BlurBehind*);
    HRESULT (WINAPI *OleInitialize)(void*);
    HRESULT (WINAPI *RegisterDragDrop)(HWND, void*);
    HRESULT (WINAPI *RevokeDragDrop)(HWND);
    void (WINAPI *ReleaseStgMedium)(void*);
    LONG (WINAPI *RtlGetVersion)(FireW32OsVersion*);
    BOOL (WINAPI *SetProcessDpiAwarenessContext)(HANDLE);
    UINT (WINAPI *GetDpiForWindow)(HWND);
    BOOL (WINAPI *AdjustWindowRectExForDpi)(RECT*, DWORD, BOOL, DWORD, UINT);
    BOOL (WINAPI *SetWindowCompositionAttribute)(HWND, FireW32CompAttrib*);
    BOOL (WINAPI *GetPointerType)(UINT32, DWORD*);
    DWORD build;
} FireW32;

static void fire_dw32_proc(HMODULE m, const char* name, void* fn) { FARPROC p = m != NULL ? GetProcAddress(m, name) : NULL; memcpy(fn, &p, sizeof p); }
static FireW32* fire_dw32_lib(void) {
    static FireW32 w;
    if (!w.tried) {
        w.tried = 1;
        w.imm = LoadLibraryW(L"imm32.dll");
        fire_dw32_proc(w.imm, "ImmGetContext", &w.ImmGetContext); fire_dw32_proc(w.imm, "ImmReleaseContext", &w.ImmReleaseContext);
        fire_dw32_proc(w.imm, "ImmGetCompositionStringW", &w.ImmGetCompositionStringW); fire_dw32_proc(w.imm, "ImmSetCompositionWindow", &w.ImmSetCompositionWindow);
        fire_dw32_proc(w.imm, "ImmSetCandidateWindow", &w.ImmSetCandidateWindow); fire_dw32_proc(w.imm, "ImmNotifyIME", &w.ImmNotifyIME);
        w.dwm = LoadLibraryW(L"dwmapi.dll");
        fire_dw32_proc(w.dwm, "DwmFlush", &w.DwmFlush); fire_dw32_proc(w.dwm, "DwmSetWindowAttribute", &w.DwmSetWindowAttribute);
        fire_dw32_proc(w.dwm, "DwmExtendFrameIntoClientArea", &w.DwmExtendFrameIntoClientArea); fire_dw32_proc(w.dwm, "DwmEnableBlurBehindWindow", &w.DwmEnableBlurBehindWindow);
        w.ole = LoadLibraryW(L"ole32.dll");
        fire_dw32_proc(w.ole, "OleInitialize", &w.OleInitialize); fire_dw32_proc(w.ole, "RegisterDragDrop", &w.RegisterDragDrop);
        fire_dw32_proc(w.ole, "RevokeDragDrop", &w.RevokeDragDrop); fire_dw32_proc(w.ole, "ReleaseStgMedium", &w.ReleaseStgMedium);
        w.ntdll = GetModuleHandleW(L"ntdll.dll");
        fire_dw32_proc(w.ntdll, "RtlGetVersion", &w.RtlGetVersion);
        w.user = GetModuleHandleW(L"user32.dll");
        fire_dw32_proc(w.user, "SetProcessDpiAwarenessContext", &w.SetProcessDpiAwarenessContext); fire_dw32_proc(w.user, "GetDpiForWindow", &w.GetDpiForWindow);
        fire_dw32_proc(w.user, "AdjustWindowRectExForDpi", &w.AdjustWindowRectExForDpi); fire_dw32_proc(w.user, "SetWindowCompositionAttribute", &w.SetWindowCompositionAttribute);
        fire_dw32_proc(w.user, "GetPointerType", &w.GetPointerType);
        if (w.RtlGetVersion != NULL) { FireW32OsVersion v; memset(&v, 0, sizeof v); v.size = sizeof v; if (w.RtlGetVersion(&v) == 0) w.build = v.major >= 10 ? v.build : 0; }
        /* sizes in pixels of the screen, per monitor (before the first window; a process that already chose - spark - keeps its choice) */
        if (w.SetProcessDpiAwarenessContext != NULL) w.SetProcessDpiAwarenessContext((HANDLE)(LONG_PTR)-4);   /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 */
        else SetProcessDPIAware();
    }
    return &w;
}

static int fire_dw32_available(void) { return 1; }
static void fire_dw32_threadDone(void) {}

/* ---- OLE: what other programs drop (IDropTarget, in C) ---- */
typedef struct FireW32DropTarget FireW32DropTarget;
typedef struct FireW32DropVtbl {
    HRESULT (STDMETHODCALLTYPE *QueryInterface)(FireW32DropTarget*, const FireOleGuid*, void**);
    ULONG (STDMETHODCALLTYPE *AddRef)(FireW32DropTarget*);
    ULONG (STDMETHODCALLTYPE *Release)(FireW32DropTarget*);
    HRESULT (STDMETHODCALLTYPE *DragEnter)(FireW32DropTarget*, void*, DWORD, POINTL, DWORD*);
    HRESULT (STDMETHODCALLTYPE *DragOver)(FireW32DropTarget*, DWORD, POINTL, DWORD*);
    HRESULT (STDMETHODCALLTYPE *DragLeave)(FireW32DropTarget*);
    HRESULT (STDMETHODCALLTYPE *Drop)(FireW32DropTarget*, void*, DWORD, POINTL, DWORD*);
} FireW32DropVtbl;
/* (the methods of IDataObject this calls: GetData and QueryGetData, slots 3 and 5 after IUnknown's three) */
typedef struct FireW32DataObjVtbl {
    void* qi; void* addRef; void* release;
    HRESULT (STDMETHODCALLTYPE *GetData)(void*, FireOleFormat*, FireOleMedium*);
    void* getDataHere;
    HRESULT (STDMETHODCALLTYPE *QueryGetData)(void*, FireOleFormat*);
} FireW32DataObjVtbl;

struct FireDw32Window;
struct FireW32DropTarget { const FireW32DropVtbl* vtbl; LONG refs; struct FireDw32Window* w; int accept; };

typedef struct FireDw32Window {
    HWND hwnd;
    int winW, winH;
    int vsync, touchMouse, cursor, closed, direct, backdrop;
    double scale;
    double lastFrame;
    BITMAPINFO bmi;
    uint32_t* bgra; size_t bgraCap;
    int bgraW, bgraH, bgraBackdrop;   /* what `bgra` holds (an area is only converted into the same) */
    int mods;
    int textX, textY, textW, textH, composing;
    WCHAR highSurrogate;
    int tracking;          /* TrackMouseEvent is on: a leave will come */
    int touchMouseFinger;  /* (pointer id + 1 of the finger that Windows makes the mouse of) */
    FireW32DropTarget drop;
    int oleDrop;
    FireDisplayQueue queue;
    FireDcompWindow dc;    /* DirectComposition (dc.on: the window shows its frames that way) */
    WCHAR* title;          /* (to make the window anew) */
    int askedBackdrop;     /* what the program asked for behind the window (made anew: asked again) */
} FireDw32Window;

static ULONG STDMETHODCALLTYPE fire_dw32_dropAddRef(FireW32DropTarget* t) { return (ULONG)InterlockedIncrement(&t->refs); }
static ULONG STDMETHODCALLTYPE fire_dw32_dropRelease(FireW32DropTarget* t) { return (ULONG)InterlockedDecrement(&t->refs); }   /* (it lives in the window) */
static HRESULT STDMETHODCALLTYPE fire_dw32_dropQI(FireW32DropTarget* t, const FireOleGuid* iid, void** out) {
    /* IUnknown {00000000-0000-0000-C000-000000000046}, IDropTarget {00000122-0000-0000-C000-000000000046} */
    if (iid->d2 == 0 && iid->d3 == 0 && iid->d4[0] == 0xC0 && (iid->d1 == 0 || iid->d1 == 0x122)) { *out = t; fire_dw32_dropAddRef(t); return S_OK; }
    *out = NULL;
    return E_NOINTERFACE;
}
static int fire_dw32_dataHas(void* data, WORD format) {
    FireOleFormat f;
    f.cfFormat = format; f.ptd = NULL; f.dwAspect = 1 /* DVASPECT_CONTENT */; f.lindex = -1; f.tymed = 1 /* TYMED_HGLOBAL */;
    return (*(const FireW32DataObjVtbl**)data)->QueryGetData(data, &f) == S_OK;
}
static void fire_dw32_clientPoint(FireDw32Window* w, POINTL pt, float* x, float* y) {
    POINT p;
    p.x = pt.x; p.y = pt.y;
    ScreenToClient(w->hwnd, &p);
    *x = (float)p.x; *y = (float)p.y;
}
static HRESULT STDMETHODCALLTYPE fire_dw32_dragEnter(FireW32DropTarget* t, void* data, DWORD keys, POINTL pt, DWORD* effect) {
    float x, y;
    (void)keys;
    t->accept = data != NULL && (fire_dw32_dataHas(data, CF_HDROP) || fire_dw32_dataHas(data, CF_UNICODETEXT));
    *effect = t->accept ? 1 /* DROPEFFECT_COPY */ : 0;
    if (t->accept) { fire_dw32_clientPoint(t->w, pt, &x, &y); fire_display_dropHover(&t->w->queue, x, y); }
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE fire_dw32_dragOver(FireW32DropTarget* t, DWORD keys, POINTL pt, DWORD* effect) {
    float x, y;
    (void)keys;
    *effect = t->accept ? 1 : 0;
    if (t->accept) { fire_dw32_clientPoint(t->w, pt, &x, &y); fire_display_dropHover(&t->w->queue, x, y); }
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE fire_dw32_dragLeave(FireW32DropTarget* t) { fire_display_dropLeave(&t->w->queue); return S_OK; }

/* The paths of a DROPFILES block (CF_HDROP), one per line (malloc, UTF-8; NULL: none). */
static char* fire_dw32_hdropPaths(const unsigned char* drop, SIZE_T total) {
    char* paths = NULL;
    DWORD offset;
    BOOL wide;
    if (drop == NULL || total < 20) return NULL;
    offset = *(const DWORD*)drop;          /* DROPFILES.pFiles */
    wide = *(const BOOL*)(drop + 16);      /* DROPFILES.fWide */
    if (offset < 20 || offset >= total) return NULL;
    if (wide) {
        const WCHAR* p = (const WCHAR*)(const void*)(drop + offset);
        const WCHAR* end = (const WCHAR*)(const void*)(drop + total);
        while (p < end && *p) {
            int len = 0;
            char* line;
            while (p + len < end && p[len]) len++;
            line = fire_display_utf16To8((const uint16_t*)p, (size_t)len);
            if (line != NULL) { paths = fire_display_addLine(paths, line, strlen(line)); free(line); }
            p += len + 1;
        }
    } else {
        const char* p = (const char*)(drop + offset);
        const char* end = (const char*)(drop + total);
        while (p < end && *p) {
            size_t len = 0;
            while (p + len < end && p[len]) len++;
            paths = fire_display_addLine(paths, p, len);
            p += len + 1;
        }
    }
    return paths;
}

static HRESULT STDMETHODCALLTYPE fire_dw32_dropDrop(FireW32DropTarget* t, void* data, DWORD keys, POINTL pt, DWORD* effect) {
    FireW32* lib = fire_dw32_lib();
    FireOleFormat f;
    FireOleMedium m;
    float x, y;
    (void)keys;
    fire_dw32_clientPoint(t->w, pt, &x, &y);
    *effect = 0;
    f.ptd = NULL; f.dwAspect = 1; f.lindex = -1; f.tymed = 1;
    f.cfFormat = CF_HDROP;
    memset(&m, 0, sizeof m);
    if (data != NULL && (*(const FireW32DataObjVtbl**)data)->GetData(data, &f, &m) == S_OK) {
        const unsigned char* p = (const unsigned char*)GlobalLock(m.hGlobal);
        char* paths = fire_dw32_hdropPaths(p, GlobalSize(m.hGlobal));
        GlobalUnlock(m.hGlobal);
        if (lib->ReleaseStgMedium) lib->ReleaseStgMedium(&m);
        if (paths != NULL) { fire_display_dropFile(&t->w->queue, paths, strlen(paths)); free(paths); *effect = 1; }
    }
    f.cfFormat = CF_UNICODETEXT;
    memset(&m, 0, sizeof m);
    if (*effect == 0 && data != NULL && (*(const FireW32DataObjVtbl**)data)->GetData(data, &f, &m) == S_OK) {
        const WCHAR* s = (const WCHAR*)GlobalLock(m.hGlobal);
        if (s != NULL) {
            size_t n = 0, max = GlobalSize(m.hGlobal) / 2;
            char* text;
            while (n < max && s[n]) n++;
            text = fire_display_utf16To8((const uint16_t*)s, n);
            if (text != NULL) { fire_display_dropText(&t->w->queue, text, strlen(text)); free(text); *effect = 1; }
        }
        GlobalUnlock(m.hGlobal);
        if (lib->ReleaseStgMedium) lib->ReleaseStgMedium(&m);
    }
    fire_display_dropDone(&t->w->queue, x, y);
    return S_OK;
}
static const FireW32DropVtbl fire_dw32DropVtbl = { fire_dw32_dropQI, fire_dw32_dropAddRef, fire_dw32_dropRelease, fire_dw32_dragEnter, fire_dw32_dragOver, fire_dw32_dragLeave, fire_dw32_dropDrop };

/* ---- the window ---- */
static void fire_dw32_init(FireDw32Window* w) {
    memset(w, 0, sizeof *w);
    w->vsync = 1; w->touchMouse = 1; w->cursor = 0; w->scale = 1.0;
}

static double fire_dw32_now(void) {
    static LARGE_INTEGER freq;
    LARGE_INTEGER c;
    if (freq.QuadPart == 0) QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)freq.QuadPart;
}

/* The modifiers that are down now (the left and right keys apart; the locks). */
static int fire_dw32_mods(void) {
    int m = 0;
    if (GetKeyState(VK_LSHIFT) & 0x8000) m |= FIRE_MOD_LSHIFT;
    if (GetKeyState(VK_RSHIFT) & 0x8000) m |= FIRE_MOD_RSHIFT;
    if (GetKeyState(VK_LCONTROL) & 0x8000) m |= FIRE_MOD_LCTRL;
    if (GetKeyState(VK_RCONTROL) & 0x8000) m |= FIRE_MOD_RCTRL;
    if (GetKeyState(VK_LMENU) & 0x8000) m |= FIRE_MOD_LALT;
    if (GetKeyState(VK_RMENU) & 0x8000) m |= FIRE_MOD_RALT;
    if (GetKeyState(VK_LWIN) & 0x8000) m |= FIRE_MOD_LGUI;
    if (GetKeyState(VK_RWIN) & 0x8000) m |= FIRE_MOD_RGUI;
    if (GetKeyState(VK_CAPITAL) & 1) m |= FIRE_MOD_CAPS;
    if (GetKeyState(VK_NUMLOCK) & 1) m |= FIRE_MOD_NUM;
    if (GetKeyState(VK_SCROLL) & 1) m |= FIRE_MOD_SCROLL;
    return m;
}

static void fire_dw32_imePlace(FireDw32Window* w) {
    FireW32* lib = fire_dw32_lib();
    HANDLE ime;
    if (lib->ImmGetContext == NULL || w->hwnd == NULL) return;
    ime = lib->ImmGetContext(w->hwnd);
    if (ime == NULL) return;
    if (lib->ImmSetCompositionWindow) {
        FireW32CompForm f;
        f.dwStyle = 0x0002;   /* CFS_POINT */
        f.ptCurrentPos.x = w->textX; f.ptCurrentPos.y = w->textY;
        SetRect(&f.rcArea, 0, 0, 0, 0);
        lib->ImmSetCompositionWindow(ime, &f);
    }
    if (lib->ImmSetCandidateWindow) {
        FireW32CandForm c;
        c.dwIndex = 0; c.dwStyle = 0x0080;   /* CFS_EXCLUDE: below the text cursor, not over it */
        c.ptCurrentPos.x = w->textX; c.ptCurrentPos.y = w->textY;
        SetRect(&c.rcArea, w->textX, w->textY, w->textX + (w->textW > 0 ? w->textW : 1), w->textY + (w->textH > 0 ? w->textH : 16));
        lib->ImmSetCandidateWindow(ime, &c);
    }
    lib->ImmReleaseContext(w->hwnd, ime);
}

/* The composition (GCS_COMPSTR) or the result (GCS_RESULTSTR) as UTF-8 (malloc; NULL: none). */
static char* fire_dw32_imeString(HWND hwnd, DWORD which, int* cursor) {
    FireW32* lib = fire_dw32_lib();
    HANDLE ime = lib->ImmGetContext ? lib->ImmGetContext(hwnd) : NULL;
    char* out = NULL;
    if (ime == NULL) return NULL;
    {
        LONG bytes = lib->ImmGetCompositionStringW(ime, which, NULL, 0);
        if (bytes > 0) {
            WCHAR* s = (WCHAR*)malloc((size_t)bytes + 2);
            if (s != NULL) {
                lib->ImmGetCompositionStringW(ime, which, s, (DWORD)bytes);
                out = fire_display_utf16To8((const uint16_t*)s, (size_t)bytes / 2);
                if (cursor != NULL) {
                    /* (the cursor in UTF-16 units: in characters for the program) */
                    LONG pos = lib->ImmGetCompositionStringW(ime, 0x0080 /* GCS_CURSORPOS */, NULL, 0), k;
                    int chars = 0;
                    for (k = 0; k < pos && k < bytes / 2; k++) if (!(s[k] >= 0xDC00 && s[k] <= 0xDFFF)) chars++;
                    *cursor = chars;
                }
                free(s);
            }
        }
    }
    lib->ImmReleaseContext(hwnd, ime);
    return out;
}

static LRESULT CALLBACK fire_dw32_wndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    FireDw32Window* w = (FireDw32Window*)(LONG_PTR)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    if (w == NULL) {
        if (msg == WM_NCCREATE) {
            CREATESTRUCTW* cs = (CREATESTRUCTW*)lp;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)cs->lpCreateParams);
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
    switch (msg) {
        case WM_CLOSE: fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE_REQUEST); return 0;
        case WM_DESTROY: w->closed = 1; return 0;
        case WM_ERASEBKGND: return 1;
        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);
            if (w->bgra != NULL && !w->direct && !w->dc.on)
                StretchDIBits(dc, 0, 0, w->winW, w->winH, 0, 0, w->bmi.bmiHeader.biWidth, -w->bmi.bmiHeader.biHeight, w->bgra, &w->bmi, DIB_RGB_COLORS, SRCCOPY);
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_SIZE:
            if (wp != SIZE_MINIMIZED) {
                int cw = (int)(short)LOWORD(lp), ch = (int)(short)HIWORD(lp);
                if (cw > 0 && ch > 0 && (cw != w->winW || ch != w->winH)) { w->winW = cw; w->winH = ch; fire_display_queueResize(&w->queue, cw, ch); }
            }
            return 0;
        case 0x02E0: {   /* WM_DPICHANGED: the new scale, and the size Windows suggests for it */
            RECT* r = (RECT*)lp;
            w->scale = LOWORD(wp) / 96.0;
            fire_display_queueScale(&w->queue, w->scale);
            SetWindowPos(hwnd, NULL, r->left, r->top, r->right - r->left, r->bottom - r->top, SWP_NOZORDER | SWP_NOACTIVATE);
            return 0;
        }
        case WM_SETFOCUS: fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_GAINED); return 0;
        case WM_KILLFOCUS: w->mods = 0; fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_LOST); return 0;
        case WM_SETCURSOR:
            if (LOWORD(lp) == HTCLIENT) {
                /* IDC_ARROW, IBEAM, WAIT, CROSS, APPSTARTING, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND (the numbers: windows.h names them as char* without UNICODE) */
                static const WORD ids[FIRE_CURSOR_COUNT] = { 32512, 32513, 32514, 32515, 32650, 32642, 32643, 32644, 32645, 32646, 32648, 32649 };
                SetCursor(LoadCursorW(NULL, MAKEINTRESOURCEW(ids[w->cursor >= 0 && w->cursor < FIRE_CURSOR_COUNT ? w->cursor : 0])));
                return TRUE;
            }
            break;
        case WM_KEYDOWN: case WM_SYSKEYDOWN: case WM_KEYUP: case WM_SYSKEYUP: {
            int down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            int sc = fire_key_fromWin32((unsigned)((lp >> 16) & 0xFF), (int)((lp >> 24) & 1));
            UINT ch;
            if (sc == FIRE_SC_UNKNOWN) {
                /* (keys without a scan code of their own: by their virtual key) */
                switch (wp) {
                    case VK_PAUSE: sc = FIRE_SC_PAUSE; break;
                    case VK_SNAPSHOT: sc = FIRE_SC_PRINTSCREEN; break;
                    default: break;
                }
            }
            ch = MapVirtualKeyW((UINT)wp, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFFu;
            fire_display_queueKey(&w->queue, down, fire_key_code(sc, ch), sc, fire_dw32_mods(), down && (lp & 0x40000000) != 0);
            /* (Alt+F4 and the menu of the window stay Windows'; F10 alone does not open the menu) */
            if (msg == WM_SYSKEYDOWN && wp == VK_F4) break;
            if (wp == VK_F10 || wp == VK_MENU) return 0;
            if (msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP) break;
            return 0;
        }
        case WM_CHAR: case WM_SYSCHAR: {
            WCHAR c = (WCHAR)wp;
            if (c >= 0xD800 && c <= 0xDBFF) { w->highSurrogate = c; return 0; }
            if (c >= 0xDC00 && c <= 0xDFFF && w->highSurrogate) {
                uint32_t cp = 0x10000u + (((uint32_t)w->highSurrogate - 0xD800u) << 10) + ((uint32_t)c - 0xDC00u);
                w->highSurrogate = 0;
                fire_display_queueCodepoint(&w->queue, cp);
                return 0;
            }
            w->highSurrogate = 0;
            if (c >= 32 && c != 127) fire_display_queueCodepoint(&w->queue, c);
            if (msg == WM_SYSCHAR) break;
            return 0;
        }
        case WM_IME_SETCONTEXT:
            /* the program draws the composition itself */
            lp &= ~(LPARAM)0x80000000;   /* ISC_SHOWUICOMPOSITIONWINDOW */
            return DefWindowProcW(hwnd, msg, wp, lp);
        case WM_IME_STARTCOMPOSITION: w->composing = 1; fire_dw32_imePlace(w); return 0;
        case WM_IME_COMPOSITION: {
            int cursor = 0;
            if (lp & 0x0800 /* GCS_RESULTSTR */) {
                char* done = fire_dw32_imeString(hwnd, 0x0800, NULL);
                if (done != NULL) { fire_display_queueText(&w->queue, done, strlen(done)); free(done); }
            }
            if (lp & 0x0008 /* GCS_COMPSTR */) {
                char* comp = fire_dw32_imeString(hwnd, 0x0008, &cursor);
                fire_display_queueEditing(&w->queue, comp, comp ? strlen(comp) : 0, cursor, 0);
                free(comp);
            } else if (lp & 0x0800) fire_display_queueEditing(&w->queue, NULL, 0, 0, 0);
            fire_dw32_imePlace(w);
            return 0;
        }
        case WM_IME_ENDCOMPOSITION: if (w->composing) fire_display_queueEditing(&w->queue, NULL, 0, 0, 0); w->composing = 0; return 0;
        case WM_MOUSEMOVE: case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_MBUTTONDOWN: case WM_MBUTTONUP:
        case WM_XBUTTONDOWN: case WM_XBUTTONUP: case WM_LBUTTONDBLCLK: case WM_RBUTTONDBLCLK: case WM_MBUTTONDBLCLK: case WM_XBUTTONDBLCLK: {
            float x = (float)(short)LOWORD(lp), y = (float)(short)HIWORD(lp);
            /* (a mouse message Windows made of a finger: only with TouchMouse) */
            if (!w->touchMouse && ((DWORD)GetMessageExtraInfo() & 0xFFFFFF80u) == 0xFF515780u) return 0;
            if (msg == WM_MOUSEMOVE) {
                if (!w->tracking) {
                    TRACKMOUSEEVENT t;
                    t.cbSize = sizeof t; t.dwFlags = TME_LEAVE; t.hwndTrack = hwnd; t.dwHoverTime = 0;
                    if (TrackMouseEvent(&t)) { w->tracking = 1; fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_ENTER); }
                }
                fire_display_queueMotion(&w->queue, x, y);
            } else {
                int b = (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_LBUTTONDBLCLK) ? FIRE_BUTTON_LEFT
                      : (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_RBUTTONDBLCLK) ? FIRE_BUTTON_RIGHT
                      : (msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP || msg == WM_MBUTTONDBLCLK) ? FIRE_BUTTON_MIDDLE
                      : HIWORD(wp) == 1 ? FIRE_BUTTON_X1 : FIRE_BUTTON_X2;
                int down = msg != WM_LBUTTONUP && msg != WM_RBUTTONUP && msg != WM_MBUTTONUP && msg != WM_XBUTTONUP;
                fire_display_queueButton(&w->queue, down, b, x, y);
                if (down) SetCapture(hwnd); else if (w->queue.buttons == 0) ReleaseCapture();
            }
            return (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP) ? TRUE : 0;
        }
        case WM_MOUSELEAVE: w->tracking = 0; fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_LEAVE); return 0;
        case WM_MOUSEWHEEL: case 0x020E /* WM_MOUSEHWHEEL */: {
            POINT p;
            float notches = (float)(short)HIWORD(wp) / 120.0f;
            p.x = (short)LOWORD(lp); p.y = (short)HIWORD(lp);
            ScreenToClient(hwnd, &p);
            if (msg == WM_MOUSEWHEEL) fire_display_queueScroll(&w->queue, 0, notches, (float)p.x, (float)p.y);
            else fire_display_queueScroll(&w->queue, notches, 0, (float)p.x, (float)p.y);
            return 0;
        }
        case 0x0246: case 0x0245: case 0x0247: {   /* WM_POINTERDOWN, WM_POINTERUPDATE, WM_POINTERUP */
            FireW32* lib = fire_dw32_lib();
            UINT32 id = (UINT32)LOWORD(wp);
            DWORD type = 0;
            if (lib->GetPointerType != NULL && lib->GetPointerType(id, &type) && type == 2 /* PT_TOUCH */) {
                POINT p;
                int ev = msg == 0x0246 ? FIRE_EV_TOUCH_DOWN : msg == 0x0247 ? FIRE_EV_TOUCH_UP : FIRE_EV_TOUCH_MOVE;
                p.x = (short)LOWORD(lp); p.y = (short)HIWORD(lp);
                ScreenToClient(hwnd, &p);
                fire_display_queueTouch(&w->queue, ev, (long long)id, (float)p.x, (float)p.y, ev == FIRE_EV_TOUCH_UP ? 0.0f : 1.0f);
                /* Windows makes mouse messages of the first finger when the message goes on to it (TouchMouse); without, the finger is only a finger */
                if (w->touchMouse) break;
                return 0;
            }
            break;
        }
        default: break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static const WCHAR* fire_dw32_class(void) {
    static int registered = 0;
    if (!registered) {
        WNDCLASSEXW wc;
        memset(&wc, 0, sizeof wc);
        wc.cbSize = sizeof wc;
        wc.style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS;
        wc.lpfnWndProc = fire_dw32_wndProc;
        wc.hInstance = GetModuleHandleW(NULL);
        wc.hCursor = LoadCursorW(NULL, MAKEINTRESOURCEW(32512));   /* IDC_ARROW */
        wc.hIcon = LoadIconW(GetModuleHandleW(NULL), MAKEINTRESOURCEW(1));
        if (wc.hIcon == NULL) wc.hIcon = LoadIconW(NULL, MAKEINTRESOURCEW(32512));   /* IDI_APPLICATION */
        wc.lpszClassName = L"fireWindow";
        RegisterClassExW(&wc);
        registered = 1;
    }
    return L"fireWindow";
}

static WCHAR* fire_dw32_wide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    WCHAR* out = (WCHAR*)malloc(sizeof(WCHAR) * (size_t)(n > 0 ? n : 1));
    if (out == NULL) return NULL;
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, s, -1, out, n); else out[0] = 0;
    return out;
}

/* The outer size of a window whose client area is width x height pixels (on the screen of the window). */
static void fire_dw32_outer(FireDw32Window* w, int width, int height, int* ow, int* oh) {
    FireW32* lib = fire_dw32_lib();
    RECT r;
    DWORD style = WS_OVERLAPPEDWINDOW;
    SetRect(&r, 0, 0, width, height);
    if (lib->AdjustWindowRectExForDpi != NULL && lib->GetDpiForWindow != NULL && w->hwnd != NULL) lib->AdjustWindowRectExForDpi(&r, style, FALSE, 0, lib->GetDpiForWindow(w->hwnd));
    else AdjustWindowRectEx(&r, style, FALSE, 0);
    *ow = r.right - r.left; *oh = r.bottom - r.top;
}

/* The title bar in the dark mode of the system, when it is on (Windows 10 20H1 and later: attribute 20; before: 19). */
static void fire_dw32_darkTitle(FireDw32Window* w) {
    FireW32* lib = fire_dw32_lib();
    FireSystemTheme t;
    BOOL dark;
    if (lib->DwmSetWindowAttribute == NULL) return;
    fire_display_systemTheme(&t);
    dark = t.dark ? TRUE : FALSE;
    if (lib->DwmSetWindowAttribute(w->hwnd, 20, &dark, sizeof dark) != S_OK) lib->DwmSetWindowAttribute(w->hwnd, 19, &dark, sizeof dark);
}

/* Makes the OS window (`ex`: WS_EX_NOREDIRECTIONBITMAP for DirectComposition) with its outer size at (x, y) - CW_USEDEFAULT: where Windows puts it - and what belongs
 * to it: the dark title bar, what other programs drop. 0: it failed. */
static int fire_dw32_create(FireDw32Window* w, DWORD ex, int x, int y, int ow, int oh) {
    FireW32* lib = fire_dw32_lib();
    w->hwnd = CreateWindowExW(ex, fire_dw32_class(), w->title ? w->title : L"", WS_OVERLAPPEDWINDOW, x, y, ow, oh, NULL, NULL, GetModuleHandleW(NULL), w);
    if (w->hwnd == NULL) return 0;
    fire_dw32_darkTitle(w);
    /* what other programs drop: OLE (positions while it is dragged, files and text) on this thread */
    w->oleDrop = 0;
    if (lib->OleInitialize != NULL && lib->RegisterDragDrop != NULL) {
        HRESULT ole = lib->OleInitialize(NULL);
        if (ole == S_OK || ole == S_FALSE) {
            w->drop.vtbl = &fire_dw32DropVtbl; w->drop.refs = 1; w->drop.w = w;
            w->oleDrop = lib->RegisterDragDrop(w->hwnd, &w->drop) == S_OK;
        }
    }
    return 1;
}

/* Lets go of the OS window (the program's window stays). */
static void fire_dw32_destroy(FireDw32Window* w) {
    FireW32* lib = fire_dw32_lib();
    fire_dcomp_close(&w->dc);
    if (w->hwnd == NULL) return;
    if (w->oleDrop && lib->RevokeDragDrop) lib->RevokeDragDrop(w->hwnd);
    SetWindowLongPtrW(w->hwnd, GWLP_USERDATA, 0);
    DestroyWindow(w->hwnd);
    w->hwnd = NULL;
}

static int fire_dw32_setBackdrop(FireDw32Window* w, int kind);

/* DirectComposition failed for the window (or its device went away): the OS window is made anew without it, where it was - GDI shows its frames from now on. */
static void fire_dw32_toGdi(FireDw32Window* w) {
    RECT r;
    int visible;
    if (w->hwnd == NULL) return;
    GetWindowRect(w->hwnd, &r);
    visible = IsWindowVisible(w->hwnd) != 0;
    fire_dw32_destroy(w);
    w->bgraW = 0;   /* (the next frame is shown whole) */
    if (!fire_dw32_create(w, 0, r.left, r.top, r.right - r.left, r.bottom - r.top)) { w->closed = 1; return; }
    if (w->askedBackdrop > FIRE_BACKDROP_NONE) fire_dw32_setBackdrop(w, w->askedBackdrop);
    if (visible) ShowWindow(w->hwnd, SW_SHOWNOACTIVATE);
}

static int fire_dw32_open(FireDw32Window* w, const char* title, int width, int height, int vsync) {
    FireW32* lib = fire_dw32_lib();
    int ow, oh, dcomp;
    if (width <= 0 || height <= 0) return 0;
    fire_display_queueInit(&w->queue);
    w->vsync = vsync;
    w->winW = width; w->winH = height;
    free(w->title);
    w->title = fire_dw32_wide(title ? title : "");
    fire_dw32_outer(w, width, height, &ow, &oh);
    /* (DirectComposition: decided before the window is made - its style depends on it) */
    dcomp = fire_dcomp_available();
    if (!fire_dw32_create(w, dcomp ? 0x00200000u /* WS_EX_NOREDIRECTIONBITMAP */ : 0, CW_USEDEFAULT, CW_USEDEFAULT, ow, oh)) return 0;
    if (dcomp && !fire_dcomp_open(&w->dc, w->hwnd)) {
        /* (a window without a bitmap of GDI shows nothing without DirectComposition: made anew without) */
        fire_dw32_destroy(w);
        if (!fire_dw32_create(w, 0, CW_USEDEFAULT, CW_USEDEFAULT, ow, oh)) return 0;
    }
    /* the client area at the asked size on the window's own screen (its scale may differ from the primary one's) */
    if (lib->GetDpiForWindow != NULL) {
        UINT dpi = lib->GetDpiForWindow(w->hwnd);
        w->scale = dpi > 0 ? dpi / 96.0 : 1.0;
        fire_dw32_outer(w, width, height, &ow, &oh);
        SetWindowPos(w->hwnd, NULL, 0, 0, ow, oh, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
    w->winW = width; w->winH = height;
    ShowWindow(w->hwnd, SW_SHOW);
    UpdateWindow(w->hwnd);
#ifdef FIRE_DISPLAY_HAS_SELFTEST
    fire_display_selftest(&w->queue, width, height);
#endif
    return 1;
}

static void fire_dw32_close(FireDw32Window* w) {
    fire_dw32_destroy(w);
    free(w->title); w->title = NULL;
    free(w->bgra); w->bgra = NULL; w->bgraCap = 0;
    fire_display_queueFree(&w->queue);
}

static void fire_dw32_setVSync(FireDw32Window* w, int v) { w->vsync = v; }
static void fire_dw32_setTouchMouse(FireDw32Window* w, int v) { w->touchMouse = v; }
static void fire_dw32_setSize(FireDw32Window* w, int width, int height) {
    int ow, oh;
    if (w->hwnd == NULL) return;
    w->winW = width; w->winH = height;   /* (no resize event comes back for the program's own size) */
    fire_dw32_outer(w, width, height, &ow, &oh);
    SetWindowPos(w->hwnd, NULL, 0, 0, ow, oh, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
}
static void fire_dw32_setCursor(FireDw32Window* w, int c) {
    POINT p;
    RECT r;
    w->cursor = c;
    /* (shown at once when the mouse is over the client area; else at the next WM_SETCURSOR) */
    if (w->hwnd != NULL && GetCursorPos(&p) && ScreenToClient(w->hwnd, &p) && GetClientRect(w->hwnd, &r) && PtInRect(&r, p)) SendMessageW(w->hwnd, WM_SETCURSOR, (WPARAM)w->hwnd, MAKELPARAM(HTCLIENT, WM_MOUSEMOVE));
}
static void fire_dw32_setTextInputRect(FireDw32Window* w, int x, int y, int width, int height) {
    w->textX = x; w->textY = y; w->textW = width; w->textH = height;
    if (w->composing) fire_dw32_imePlace(w);
}

static int fire_dw32_setBackdrop(FireDw32Window* w, int kind) {
    FireW32* lib = fire_dw32_lib();
    FireW32Margins all = { -1, -1, -1, -1 }, none = { 0, 0, 0, 0 };
    int got = FIRE_BACKDROP_NONE, type;
    w->askedBackdrop = kind;
    w->dc.full = 1;   /* (the next frame goes in whole - premultiplied or not) */
    if (w->hwnd == NULL || (lib->DwmEnableBlurBehindWindow == NULL && !w->dc.on)) { w->backdrop = FIRE_BACKDROP_NONE; return w->backdrop; }
    /* back to plain first */
    if (lib->SetWindowCompositionAttribute != NULL) { FireW32Accent a = { 0, 0, 0, 0 }; FireW32CompAttrib c; c.attrib = 19; c.data = &a; c.size = sizeof a; lib->SetWindowCompositionAttribute(w->hwnd, &c); }
    type = 1;   /* DWMSBT_NONE */
    if (lib->DwmSetWindowAttribute != NULL && lib->build >= 22621) lib->DwmSetWindowAttribute(w->hwnd, 38, &type, sizeof type);
    if (lib->DwmExtendFrameIntoClientArea != NULL) lib->DwmExtendFrameIntoClientArea(w->hwnd, &none);
    if (w->dc.on) got = kind > FIRE_BACKDROP_NONE ? FIRE_BACKDROP_TRANSPARENT : FIRE_BACKDROP_NONE;   /* (DirectComposition: the swap chain's alpha is the transparency) */
    else {
        FireW32BlurBehind b;
        HRGN empty = CreateRectRgn(0, 0, -1, -1);
        b.dwFlags = 0x1 | 0x2;   /* DWM_BB_ENABLE | DWM_BB_BLURREGION */
        b.fEnable = kind > FIRE_BACKDROP_NONE ? TRUE : FALSE;
        b.hRgnBlur = empty;
        b.fTransitionOnMaximized = FALSE;
        /* (an empty region: no blur, but the window's alpha counts - per-pixel transparency) */
        if (lib->DwmEnableBlurBehindWindow(w->hwnd, &b) == S_OK && kind > FIRE_BACKDROP_NONE) got = FIRE_BACKDROP_TRANSPARENT;
        DeleteObject(empty);
    }
    if (kind >= FIRE_BACKDROP_BLUR && got == FIRE_BACKDROP_TRANSPARENT) {
        if (kind >= FIRE_BACKDROP_ACRYLIC && lib->DwmSetWindowAttribute != NULL && lib->build >= 22621) {
            /* Windows 11 22H2: the system backdrop - Mica (2) or acrylic (3) - behind the whole window */
            type = kind == FIRE_BACKDROP_MICA ? 2 : 3;
            if (lib->DwmExtendFrameIntoClientArea != NULL) lib->DwmExtendFrameIntoClientArea(w->hwnd, &all);
            if (lib->DwmSetWindowAttribute(w->hwnd, 38, &type, sizeof type) == S_OK) got = kind;
        } else if (kind == FIRE_BACKDROP_MICA && lib->DwmSetWindowAttribute != NULL && lib->build >= 22000) {
            BOOL on = TRUE;   /* Windows 11 21H2: DWMWA_MICA_EFFECT */
            if (lib->DwmExtendFrameIntoClientArea != NULL) lib->DwmExtendFrameIntoClientArea(w->hwnd, &all);
            if (lib->DwmSetWindowAttribute(w->hwnd, 1029, &on, sizeof on) == S_OK) got = FIRE_BACKDROP_MICA;
        }
        if (got < FIRE_BACKDROP_BLUR && lib->SetWindowCompositionAttribute != NULL) {
            /* Windows 10: the accent policy - acrylic (4, 1803 and later; a light veil of the tint) or blur behind (3) */
            FireW32Accent a;
            FireW32CompAttrib c;
            int acrylic = kind >= FIRE_BACKDROP_ACRYLIC && lib->build >= 17134;
            a.state = acrylic ? 4 : 3; a.flags = 2; a.gradient = acrylic ? 0x01000000u : 0; a.animation = 0;
            c.attrib = 19; c.data = &a; c.size = sizeof a;   /* WCA_ACCENT_POLICY */
            if (lib->SetWindowCompositionAttribute(w->hwnd, &c)) got = acrylic ? FIRE_BACKDROP_ACRYLIC : FIRE_BACKDROP_BLUR;
        }
    }
    w->backdrop = got;
    InvalidateRect(w->hwnd, NULL, FALSE);
    return got;
}

static double fire_dw32_scale(FireDw32Window* w) { return w->scale; }

static int fire_dw32_pump(FireDw32Window* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    MSG m;
    if (w->hwnd == NULL) return 0;
    /* (the messages of this thread: every window of it takes its own into its queue) */
    while (PeekMessageW(&m, NULL, 0, 0, PM_REMOVE)) {
        if (m.message == WM_QUIT) { PostQuitMessage((int)m.wParam); break; }
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    if (w->closed) fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE);
    fire_display_queueTake(&w->queue, out, w->winW, w->winH, fbWidth, fbHeight);
    return !w->queue.quit;
}

/* Shows the pixels (R,G,B,A per 32-bit word), stretched to the client area: converted to B,G,R,A (premultiplied when the window shows what lies behind it). */
/* Shows the frame; only the area (ax, ay, aw, ah) changed since the last one: when the size is the same, only it is converted and handed to the window (the bitmap
 * keeps the rest; WM_PAINT shows all of it). */
static void fire_dw32_presentArea(FireDw32Window* w, const uint32_t* rgba, int width, int height, int ax, int ay, int aw, int ah) {
    FireW32* lib = fire_dw32_lib();
    size_t n;
    int row, col, c0 = 0, c1 = width, r0 = 0, r1 = height;
    HDC dc;
    if (w->hwnd == NULL || w->direct || rgba == NULL || width <= 0 || height <= 0) return;
    if (w->dc.on) {
        /* DirectComposition: the area into the window's picture, presented (Present waits for the screen with VSync) */
        w->dc.premultiplied = w->backdrop != FIRE_BACKDROP_NONE;
        w->dc.viewW = w->winW; w->dc.viewH = w->winH;
        w->dc.vsync = w->vsync;
        if (fire_dcomp_presentPixels(&w->dc, rgba, width, height, ax, ay, aw, ah)) return;
        if (!w->dc.lost) return;
        fire_dw32_toGdi(w);   /* (the device went away: GDI from now on) */
        if (w->hwnd == NULL) return;
        ax = 0; ay = 0; aw = width; ah = height;
    }
    n = (size_t)width * (size_t)height;
    if (n > w->bgraCap) {
        uint32_t* grown = (uint32_t*)realloc(w->bgra, n * 4);
        if (grown == NULL) return;
        w->bgra = grown; w->bgraCap = n;
        w->bgraW = 0;
    }
    if (w->bgraW == width && w->bgraH == height && w->bgraBackdrop == w->backdrop) {
        c0 = ax < 0 ? 0 : ax > width ? width : ax; c1 = ax + aw < 0 ? 0 : ax + aw > width ? width : ax + aw;
        r0 = ay < 0 ? 0 : ay > height ? height : ay; r1 = ay + ah < 0 ? 0 : ay + ah > height ? height : ay + ah;
    }
    w->bgraW = width; w->bgraH = height; w->bgraBackdrop = w->backdrop;
    for (row = r0; row < r1; row++) {
        const uint32_t* src = rgba + (size_t)row * width;
        uint32_t* d = w->bgra + (size_t)row * width;
        if (w->backdrop == FIRE_BACKDROP_NONE)
            for (col = c0; col < c1; col++) { uint32_t v = src[col]; d[col] = 0xFF000000u | ((v & 0xFFu) << 16) | (v & 0xFF00u) | ((v >> 16) & 0xFFu); }
        else
            for (col = c0; col < c1; col++) {
                uint32_t v = src[col], a = v >> 24;
                d[col] = (a << 24) | (((v & 0xFFu) * a / 255u) << 16) | ((((v >> 8) & 0xFFu) * a / 255u) << 8) | (((v >> 16) & 0xFFu) * a / 255u);
            }
    }
    memset(&w->bmi, 0, sizeof w->bmi);
    w->bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    w->bmi.bmiHeader.biWidth = width;
    w->bmi.bmiHeader.biHeight = -height;   /* (top-down) */
    w->bmi.bmiHeader.biPlanes = 1;
    w->bmi.bmiHeader.biBitCount = 32;
    w->bmi.bmiHeader.biCompression = BI_RGB;
    dc = GetDC(w->hwnd);
    if (dc != NULL) {
        if (width == w->winW && height == w->winH && (c0 > 0 || r0 > 0 || c1 < width || r1 < height)) {
            /* only the area: the band of its rows as a bitmap of its own (top-down, starting at its first row; source y 0 is then its first row whichever way
             * Windows counts), from its first column */
            if (c1 > c0 && r1 > r0) {
                BITMAPINFO band = w->bmi;
                band.bmiHeader.biHeight = -(r1 - r0);
                SetDIBitsToDevice(dc, c0, r0, (DWORD)(c1 - c0), (DWORD)(r1 - r0), c0, 0, 0, (UINT)(r1 - r0), w->bgra + (size_t)r0 * width, &band, DIB_RGB_COLORS);
            }
        }
        else if (width == w->winW && height == w->winH) SetDIBitsToDevice(dc, 0, 0, (DWORD)width, (DWORD)height, 0, 0, 0, (UINT)height, w->bgra, &w->bmi, DIB_RGB_COLORS);
        else {
            SetStretchBltMode(dc, COLORONCOLOR);
            StretchDIBits(dc, 0, 0, w->winW, w->winH, 0, 0, width, height, w->bgra, &w->bmi, DIB_RGB_COLORS, SRCCOPY);
        }
        ReleaseDC(w->hwnd, dc);
    }
    ValidateRect(w->hwnd, NULL);
    if (w->vsync) {
        if (lib->DwmFlush == NULL || lib->DwmFlush() != S_OK) {
            double now = fire_dw32_now(), next = w->lastFrame + 1.0 / 60.0;
            if (w->lastFrame > 0 && now < next) {
#ifdef FIRE_RT_HELPING_WAIT
                if (!fire_frameWait(next - now))   /* (the main program of a native build: its thread runs tasks meanwhile) */
#endif
                Sleep((DWORD)((next - now) * 1000.0));
            }
        }
        w->lastFrame = fire_dw32_now();
    }
}

static void fire_dw32_present(FireDw32Window* w, const uint32_t* rgba, int width, int height) { fire_dw32_presentArea(w, rgba, width, height, 0, 0, width, height); }

static int fire_dw32_native(FireDw32Window* w, fire_native_window* out) {
    if (w->hwnd == NULL) return 0;
    if (w->dc.lost) fire_dw32_toGdi(w);
    if (w->hwnd == NULL) return 0;
    memset(out, 0, sizeof *out);
    if (w->dc.on) {
        /* DirectComposition: the presenter draws into the window's picture (fire_dcomp_bridge), not into the HWND */
        w->dc.premultiplied = w->backdrop != FIRE_BACKDROP_NONE;
        w->dc.bridge.premultiplied = w->dc.premultiplied;
        w->dc.viewW = w->winW; w->dc.viewH = w->winH;
        w->dc.vsync = w->vsync;
        out->kind = FIRE_NATIVE_DCOMP;
        out->a = &w->dc.bridge;
    } else {
        out->kind = FIRE_NATIVE_WIN32;
        out->a = GetModuleHandleW(NULL);
    }
    out->b = w->hwnd;
    out->width = w->winW; out->height = w->winH;
    out->vsync = w->vsync;
    w->direct = 1;
    return 1;
}
static void fire_dw32_restore(FireDw32Window* w) { w->direct = 0; w->dc.full = 1; w->bgraW = 0; }
/* How the window shows its frames: 2 DirectComposition, 1 GDI (Window.PresentPath). */
static int fire_dw32_presentPath(FireDw32Window* w) { return w->dc.on ? 2 : 1; }

static int fire_dw32_startDrag(FireDw32Window* w, const char* text, const char* files) { return w->hwnd != NULL ? fire_dragout_win32(w->hwnd, text, files) : 0; }
static int fire_dw32_canStartDrag(FireDw32Window* w) { return w->hwnd != NULL; }

/* ---- the clipboard: CF_UNICODETEXT, the registered "PNG" and CF_DIB, CF_HDROP ---- */
static HWND fire_dw32_clipOwner(void) {
    static HWND owner = NULL;   /* a message-only window: SetClipboardData needs an owner */
    if (owner == NULL) owner = CreateWindowExW(0, L"STATIC", L"fire clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, NULL, NULL, NULL);
    return owner;
}
static int fire_dw32_openClipboard(HWND owner) {
    int attempt;
    for (attempt = 0; attempt < 10; attempt++) { if (OpenClipboard(owner)) return 1; Sleep(10); }
    return 0;
}
static UINT fire_dw32_pngFormat(void) { static UINT f = 0; if (f == 0) f = RegisterClipboardFormatW(L"PNG"); return f; }
static HGLOBAL fire_dw32_global(const void* data, size_t n) {
    HGLOBAL mem = GlobalAlloc(GMEM_MOVEABLE, n > 0 ? n : 1);
    void* p;
    if (mem == NULL) return NULL;
    p = GlobalLock(mem);
    if (p == NULL) { GlobalFree(mem); return NULL; }
    if (n > 0) memcpy(p, data, n);
    GlobalUnlock(mem);
    return mem;
}
/* Puts the formats there (pairs of format and HGLOBAL; what Windows did not take is freed). */
static int fire_dw32_clipPut(UINT* formats, HGLOBAL* mems, int count) {
    int k, ok = 0;
    if (!fire_dw32_openClipboard(fire_dw32_clipOwner())) { for (k = 0; k < count; k++) if (mems[k]) GlobalFree(mems[k]); return 0; }
    EmptyClipboard();
    for (k = 0; k < count; k++) {
        if (mems[k] == NULL) continue;
        if (SetClipboardData(formats[k], mems[k]) != NULL) ok = 1; else GlobalFree(mems[k]);
    }
    CloseClipboard();
    return ok;
}
static int fire_dw32_clipboardSet(const char* text) {
    WCHAR* wide = fire_dw32_wide(text);
    UINT f = CF_UNICODETEXT;
    HGLOBAL m;
    if (wide == NULL) return 0;
    m = fire_dw32_global(wide, (wcslen(wide) + 1) * sizeof(WCHAR));
    free(wide);
    return fire_dw32_clipPut(&f, &m, 1);
}
static char* fire_dw32_clipboardGet(void) {
    char* text = NULL;
    HANDLE h;
    if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !fire_dw32_openClipboard(NULL)) return NULL;
    h = GetClipboardData(CF_UNICODETEXT);
    if (h != NULL) {
        const WCHAR* s = (const WCHAR*)GlobalLock(h);
        if (s != NULL) {
            size_t n = 0, max = GlobalSize(h) / 2;
            while (n < max && s[n]) n++;
            text = fire_display_utf16To8((const uint16_t*)s, n);
            GlobalUnlock(h);
        }
    }
    CloseClipboard();
    if (text != NULL && text[0] == 0) { free(text); text = NULL; }
    return text;
}
static int fire_dw32_clipboardHas(void) { return IsClipboardFormatAvailable(CF_UNICODETEXT) != 0; }
static unsigned char* fire_dw32_clipboardGetImage(size_t* size) {
    unsigned char* image = NULL;
    HANDLE h;
    *size = 0;
    if (!fire_dw32_openClipboard(NULL)) return NULL;
    if ((h = GetClipboardData(fire_dw32_pngFormat())) != NULL) {
        const void* p = GlobalLock(h);
        SIZE_T n = GlobalSize(h);
        if (p != NULL && n > 0) { image = fire_display_copyBytes(p, n); if (image) *size = n; }
        GlobalUnlock(h);
    } else if ((h = GetClipboardData(CF_DIB)) != NULL) {
        /* a DIB as the bytes of a BMP file: the file header (14 bytes) before it */
        const BITMAPINFOHEADER* bi = (const BITMAPINFOHEADER*)GlobalLock(h);
        SIZE_T n = GlobalSize(h);
        if (bi != NULL && n >= sizeof(BITMAPINFOHEADER)) {
            DWORD colors = bi->biClrUsed ? bi->biClrUsed : (bi->biBitCount <= 8 ? 1u << bi->biBitCount : 0);
            DWORD bits = bi->biSize + colors * 4 + (bi->biCompression == 3 /* BI_BITFIELDS */ && bi->biSize == sizeof(BITMAPINFOHEADER) ? 12 : 0);
            image = (unsigned char*)malloc(n + 14 + 1);
            if (image != NULL) {
                DWORD total = (DWORD)(n + 14), offset = 14 + bits;
                image[0] = 'B'; image[1] = 'M';
                memcpy(image + 2, &total, 4); memset(image + 6, 0, 4); memcpy(image + 10, &offset, 4);
                memcpy(image + 14, bi, n);
                image[n + 14] = 0;
                *size = n + 14;
            }
        }
        if (bi != NULL) GlobalUnlock(h);
    }
    CloseClipboard();
    return image;
}
static int fire_dw32_clipboardHasImage(void) { return IsClipboardFormatAvailable(fire_dw32_pngFormat()) || IsClipboardFormatAvailable(CF_DIB); }
static int fire_dw32_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) {
    UINT formats[2];
    HGLOBAL mems[2];
    int count = 0;
    if (png == NULL || pngSize == 0) return 0;
    formats[count] = fire_dw32_pngFormat(); mems[count++] = fire_dw32_global(png, pngSize);
    if (bmp != NULL && bmpSize > 14) { formats[count] = CF_DIB; mems[count++] = fire_dw32_global(bmp + 14, bmpSize - 14); }   /* (the DIB without the file header) */
    return fire_dw32_clipPut(formats, mems, count);
}
static char* fire_dw32_clipboardGetFiles(void) {
    char* paths = NULL;
    HANDLE h;
    if (!IsClipboardFormatAvailable(CF_HDROP) || !fire_dw32_openClipboard(NULL)) return NULL;
    h = GetClipboardData(CF_HDROP);
    if (h != NULL) {
        const unsigned char* p = (const unsigned char*)GlobalLock(h);
        paths = fire_dw32_hdropPaths(p, GlobalSize(h));
        GlobalUnlock(h);
    }
    CloseClipboard();
    return paths;
}
static int fire_dw32_clipboardHasFiles(void) { return IsClipboardFormatAvailable(CF_HDROP) != 0; }
static int fire_dw32_clipboardSetFiles(const char* paths) {
    WCHAR* wide = fire_dw32_wide(paths);
    size_t n, i;
    HGLOBAL mem;
    unsigned char* drop;
    UINT f = CF_HDROP;
    if (wide == NULL) return 0;
    n = wcslen(wide);
    mem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, 20 + (n + 2) * sizeof(WCHAR));
    if (mem == NULL || (drop = (unsigned char*)GlobalLock(mem)) == NULL) { free(wide); if (mem) GlobalFree(mem); return 0; }
    *(DWORD*)(void*)drop = 20;          /* pFiles */
    *(BOOL*)(void*)(drop + 16) = TRUE;  /* fWide */
    memcpy(drop + 20, wide, n * sizeof(WCHAR));
    for (i = 0; i < n; i++) if (((WCHAR*)(void*)(drop + 20))[i] == L'\n') ((WCHAR*)(void*)(drop + 20))[i] = 0;   /* one path per line -> each ends in 0, the list in two */
    GlobalUnlock(mem);
    free(wide);
    return fire_dw32_clipPut(&f, &mem, 1);
}

#endif
