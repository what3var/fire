/* fire native platform package "std": a window on an X11 desktop (fire_dx11_*, one of the displays of fire_display_desktop.h) - without the headers of
 * X11: libX11 is loaded when the first window opens (dlopen; libXext for MIT-SHM, libXi for touch, libXcursor for the cursors of the theme are optional).
 *
 * Every window has its own connection to the X server (it belongs to the thread that opened it, as the HAL wants; nothing is shared between threads except libX11,
 * which is told so with XInitThreads). What the window shows is the framebuffer, converted to the pixels of the window (and stretched when the sizes differ), put there
 * with MIT-SHM on a local server and with XPutImage otherwise. VSync: X11 has no presentation clock for this - a frame waits until 1/60 s since the last one passed.
 * Input: keys (the scan code from the key code of the server, the key code from the keyboard layout: fire_display_keys.h), text through the input method of X (XIM:
 * compositions are drawn by the program - preedit callbacks - where the input method allows it, else at the spot the program names, else in a window of its own), the
 * mouse and the wheel, fingers (XInput 2.2), what other programs drop (XDND 5: files as text/uri-list, text as UTF-8), the cursors of the theme.
 * The clipboard has a connection and a window of its own, served by a thread: what the program put there is answered whenever another program asks (also while the
 * program does something else), large pictures in pieces (INCR) - and read the same way.
 * Behind the window: with a compositor (_NET_WM_CM_S0) the window can be transparent (a 32-bit visual: the window is made anew with it when it is asked for), and KWin
 * blurs behind it (_KDE_NET_WM_BLUR_BEHIND_REGION). */
#ifndef FIRE_DISPLAY_X11_H
#define FIRE_DISPLAY_X11_H

#include <dlfcn.h>
#include <locale.h>
#include <poll.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ipc.h>
#include <sys/shm.h>
#include <time.h>
#include <unistd.h>
#include "fire_display_common.h"
#include "fire_dragout.h"

/* ---- the parts of Xlib this needs, laid out as in <X11/Xlib.h>, <X11/extensions/XShm.h> and <X11/extensions/XInput2.h> ---- */
typedef struct FireXAny { int type; unsigned long serial; int send_event; void* display; FireXId window; } FireXAny;
typedef struct FireXKey { int type; unsigned long serial; int send_event; void* display; FireXId window, root, subwindow, time; int x, y, x_root, y_root; unsigned int state, keycode; int same_screen; } FireXKey;
typedef struct FireXButton { int type; unsigned long serial; int send_event; void* display; FireXId window, root, subwindow, time; int x, y, x_root, y_root; unsigned int state, button; int same_screen; } FireXButton;
typedef struct FireXMotion { int type; unsigned long serial; int send_event; void* display; FireXId window, root, subwindow, time; int x, y, x_root, y_root; unsigned int state; char is_hint; int same_screen; } FireXMotion;
typedef struct FireXCrossing { int type; unsigned long serial; int send_event; void* display; FireXId window, root, subwindow, time; int x, y, x_root, y_root; int mode, detail, same_screen, focus; unsigned int state; } FireXCrossing;
typedef struct FireXFocus { int type; unsigned long serial; int send_event; void* display; FireXId window; int mode, detail; } FireXFocus;
typedef struct FireXExpose { int type; unsigned long serial; int send_event; void* display; FireXId window; int x, y, width, height, count; } FireXExpose;
typedef struct FireXConfigure { int type; unsigned long serial; int send_event; void* display; FireXId event, window; int x, y, width, height, border_width; FireXId above; int override_redirect; } FireXConfigure;
typedef struct FireXSelClear { int type; unsigned long serial; int send_event; void* display; FireXId window, selection, time; } FireXSelClear;
typedef struct FireXCookie { int type; unsigned long serial; int send_event; void* display; int extension, evtype; unsigned int cookie; void* data; } FireXCookie;
typedef union FireXEv {
    int type; FireXAny xany; FireXKey xkey; FireXButton xbutton; FireXMotion xmotion; FireXCrossing xcrossing; FireXFocus xfocus; FireXExpose xexpose; FireXConfigure xconfigure;
    FireXClient xclient; FireXSelRequest xselectionrequest; FireXSelNotify xselection; FireXSelClear xselectionclear; FireXProperty xproperty; FireXCookie xcookie; long pad[24];
} FireXEv;
enum {
    FIRE_X_KEY_PRESS = 2, FIRE_X_KEY_RELEASE = 3, FIRE_X_BUTTON_PRESS = 4, FIRE_X_BUTTON_RELEASE = 5, FIRE_X_MOTION = 6, FIRE_X_ENTER = 7, FIRE_X_LEAVE = 8, FIRE_X_FOCUS_IN = 9,
    FIRE_X_FOCUS_OUT = 10, FIRE_X_EXPOSE = 12, FIRE_X_DESTROY = 17, FIRE_X_CONFIGURE = 22, FIRE_X_SELECTION_CLEAR = 29, FIRE_X_MAPPING = 34, FIRE_X_GENERIC = 35
};
typedef struct FireXImageFuncs {
    void* (*create_image)(void);
    int (*destroy_image)(void*);
    unsigned long (*get_pixel)(void*, int, int);
    int (*put_pixel)(void*, int, int, unsigned long);
    void* (*sub_image)(void*, int, int, unsigned int, unsigned int);
    int (*add_pixel)(void*, long);
} FireXImageFuncs;
typedef struct FireXImage {
    int width, height, xoffset, format;
    char* data;
    int byte_order, bitmap_unit, bitmap_bit_order, bitmap_pad, depth, bytes_per_line, bits_per_pixel;
    unsigned long red_mask, green_mask, blue_mask;
    char* obdata;
    FireXImageFuncs f;
} FireXImage;
typedef struct FireXShmInfo { unsigned long shmseg; int shmid; char* shmaddr; int readOnly; } FireXShmInfo;
typedef struct FireXVisualInfo { void* visual; unsigned long visualid; int screen, depth, c_class; unsigned long red_mask, green_mask, blue_mask; int colormap_size, bits_per_rgb; } FireXVisualInfo;
typedef struct FireXSetAttributes {
    unsigned long background_pixmap, background_pixel, border_pixmap, border_pixel;
    int bit_gravity, win_gravity, backing_store;
    unsigned long backing_planes, backing_pixel;
    int save_under;
    long event_mask, do_not_propagate_mask;
    int override_redirect;
    unsigned long colormap, cursor;
} FireXSetAttributes;
typedef struct FireXClassHint { char* res_name; char* res_class; } FireXClassHint;
typedef struct FireXPoint { short x, y; } FireXPoint;
typedef struct FireXIMCallback { char* client_data; void (*callback)(void*, char*, char*); } FireXIMCallback;
typedef struct FireXIMStyles { unsigned short count; unsigned long* styles; } FireXIMStyles;
typedef struct FireXIMText { unsigned short length; unsigned long* feedback; int encoding_is_wchar; union { char* multi_byte; void* wide_char; } string; } FireXIMText;
typedef struct FireXIMDraw { int caret, chg_first, chg_length; FireXIMText* text; } FireXIMDraw;
typedef struct FireXIMCaret { int position; int direction; int style; } FireXIMCaret;
typedef struct FireXIEventMask { int deviceid; int mask_len; unsigned char* mask; } FireXIEventMask;
typedef struct FireXIDeviceEvent {
    int type; unsigned long serial; int send_event; void* display; int extension, evtype; FireXId time; int deviceid, sourceid, detail; FireXId root, event, child;
    double root_x, root_y, event_x, event_y;
    int flags;
    struct { int mask_len; unsigned char* mask; } buttons;
    struct { int mask_len; unsigned char* mask; double* values; } valuators;
    struct { int base, latched, locked, effective; } mods, group;
} FireXIDeviceEvent;

typedef struct FireXlib {
    int tried, ok;
    void* lib;
    void* ext;      /* libXext (MIT-SHM) */
    void* xi;       /* libXi */
    void* xcursor;  /* libXcursor */
    int (*InitThreads)(void);
    void* (*OpenDisplay)(const char*);
    int (*CloseDisplay)(void*);
    int (*DefaultScreen)(void*);
    void* (*DefaultVisual)(void*, int);
    int (*DefaultDepth)(void*, int);
    FireXId (*RootWindow)(void*, int);
    int (*MatchVisualInfo)(void*, int, int, int, FireXVisualInfo*);
    FireXId (*CreateColormap)(void*, FireXId, void*, int);
    int (*FreeColormap)(void*, FireXId);
    FireXId (*CreateWindow)(void*, FireXId, int, int, unsigned, unsigned, unsigned, int, unsigned, void*, unsigned long, FireXSetAttributes*);
    int (*DestroyWindow)(void*, FireXId);
    int (*MapWindow)(void*, FireXId);
    int (*ResizeWindow)(void*, FireXId, unsigned, unsigned);
    int (*StoreName)(void*, FireXId, const char*);
    int (*SetClassHint)(void*, FireXId, FireXClassHint*);
    int (*SetWMProtocols)(void*, FireXId, FireXId*, int);
    FireXId (*InternAtom)(void*, const char*, int);
    int (*ChangeProperty)(void*, FireXId, FireXId, FireXId, int, int, const unsigned char*, int);
    int (*DeleteProperty)(void*, FireXId, FireXId);
    int (*GetWindowProperty)(void*, FireXId, FireXId, long, long, int, FireXId, FireXId*, int*, unsigned long*, unsigned long*, unsigned char**);
    int (*Pending)(void*);
    int (*PeekEvent)(void*, FireXEv*);
    int (*NextEvent)(void*, FireXEv*);
    int (*SendEvent)(void*, FireXId, int, long, FireXEv*);
    int (*Flush)(void*);
    int (*Sync)(void*, int);
    int (*Free)(void*);
    int (*ConnectionNumber)(void*);
    int (*SelectInput)(void*, FireXId, long);
    void* (*CreateGC)(void*, FireXId, unsigned long, void*);
    int (*FreeGC)(void*, void*);
    FireXImage* (*CreateImage)(void*, void*, unsigned, int, int, char*, unsigned, unsigned, int, int);
    int (*PutImage)(void*, FireXId, void*, FireXImage*, int, int, int, int, unsigned, unsigned);
    FireXId (*CreateFontCursor)(void*, unsigned);
    int (*DefineCursor)(void*, FireXId, FireXId);
    int (*FreeCursor)(void*, FireXId);
    unsigned long (*LookupKeysym)(FireXKey*, int);
    int (*FilterEvent)(FireXEv*, FireXId);
    int (*GetEventData)(void*, FireXCookie*);
    void (*FreeEventData)(void*, FireXCookie*);
    int (*QueryExtension)(void*, const char*, int*, int*, int*);
    FireXId (*GetSelectionOwner)(void*, FireXId);
    int (*SetSelectionOwner)(void*, FireXId, FireXId, FireXId);
    int (*ConvertSelection)(void*, FireXId, FireXId, FireXId, FireXId, FireXId);
    int (*TranslateCoordinates)(void*, FireXId, FireXId, int, int, int*, int*, FireXId*);
    long (*ExtendedMaxRequestSize)(void*);
    long (*MaxRequestSize)(void*);
    char* (*ResourceManagerString)(void*);
    FireXErrorHandler (*SetErrorHandler)(FireXErrorHandler);
    int (*RefreshKeyboardMapping)(void*);
    /* the input method */
    int (*SupportsLocale)(void);
    char* (*SetLocaleModifiers)(const char*);
    void* (*OpenIM)(void*, void*, char*, char*);
    int (*CloseIM)(void*);
    char* (*GetIMValues)(void*, ...);
    void* (*CreateIC)(void*, ...);
    void (*DestroyIC)(void*);
    void (*SetICFocus)(void*);
    void (*UnsetICFocus)(void*);
    char* (*SetICValues)(void*, ...);
    void* (*VaCreateNestedList)(int, ...);
    int (*Utf8LookupString)(void*, FireXKey*, char*, int, unsigned long*, int*);
    char* (*Utf8ResetIC)(void*);
    /* MIT-SHM (libXext) */
    int (*ShmQueryExtension)(void*);
    FireXImage* (*ShmCreateImage)(void*, void*, unsigned, int, char*, FireXShmInfo*, unsigned, unsigned);
    int (*ShmAttach)(void*, FireXShmInfo*);
    int (*ShmDetach)(void*, FireXShmInfo*);
    int (*ShmPutImage)(void*, FireXId, void*, FireXImage*, int, int, int, int, unsigned, unsigned, int);
    /* XInput 2 (libXi) */
    int (*XIQueryVersion)(void*, int*, int*);
    int (*XISelectEvents)(void*, FireXId, FireXIEventMask*, int);
    /* libXcursor */
    FireXId (*CursorLibraryLoadCursor)(void*, const char*);
} FireXlib;

static int fire_dx11_sym(void* lib, const char* name, void* fn) {
    void* p = lib != NULL ? dlsym(lib, name) : NULL;
    memcpy(fn, &p, sizeof p);
    return p != NULL;
}
static void* fire_dx11_open2(const char* a, const char* b) {
    void* lib = dlopen(a, RTLD_LAZY | RTLD_LOCAL);
    return lib != NULL ? lib : dlopen(b, RTLD_LAZY | RTLD_LOCAL);
}

/* libX11 (and what is there of libXext, libXi, libXcursor), loaded once; NULL without libX11. */
static FireXlib* fire_dx11_lib(void) {
    static FireXlib x;
    static pthread_mutex_t guard = PTHREAD_MUTEX_INITIALIZER;
    pthread_mutex_lock(&guard);
    if (!x.tried) {
        void* l;
        x.tried = 1;
        x.lib = l = fire_dx11_open2("libX11.so.6", "libX11.so");
        x.ok = l != NULL
            && fire_dx11_sym(l, "XInitThreads", &x.InitThreads) && fire_dx11_sym(l, "XOpenDisplay", &x.OpenDisplay) && fire_dx11_sym(l, "XCloseDisplay", &x.CloseDisplay)
            && fire_dx11_sym(l, "XDefaultScreen", &x.DefaultScreen) && fire_dx11_sym(l, "XDefaultVisual", &x.DefaultVisual) && fire_dx11_sym(l, "XDefaultDepth", &x.DefaultDepth)
            && fire_dx11_sym(l, "XRootWindow", &x.RootWindow) && fire_dx11_sym(l, "XMatchVisualInfo", &x.MatchVisualInfo) && fire_dx11_sym(l, "XCreateColormap", &x.CreateColormap)
            && fire_dx11_sym(l, "XFreeColormap", &x.FreeColormap) && fire_dx11_sym(l, "XCreateWindow", &x.CreateWindow) && fire_dx11_sym(l, "XDestroyWindow", &x.DestroyWindow)
            && fire_dx11_sym(l, "XMapWindow", &x.MapWindow) && fire_dx11_sym(l, "XResizeWindow", &x.ResizeWindow) && fire_dx11_sym(l, "XStoreName", &x.StoreName)
            && fire_dx11_sym(l, "XSetClassHint", &x.SetClassHint) && fire_dx11_sym(l, "XSetWMProtocols", &x.SetWMProtocols) && fire_dx11_sym(l, "XInternAtom", &x.InternAtom)
            && fire_dx11_sym(l, "XChangeProperty", &x.ChangeProperty) && fire_dx11_sym(l, "XDeleteProperty", &x.DeleteProperty)
            && fire_dx11_sym(l, "XGetWindowProperty", &x.GetWindowProperty) && fire_dx11_sym(l, "XPending", &x.Pending) && fire_dx11_sym(l, "XPeekEvent", &x.PeekEvent) && fire_dx11_sym(l, "XNextEvent", &x.NextEvent)
            && fire_dx11_sym(l, "XSendEvent", &x.SendEvent) && fire_dx11_sym(l, "XFlush", &x.Flush) && fire_dx11_sym(l, "XSync", &x.Sync) && fire_dx11_sym(l, "XFree", &x.Free)
            && fire_dx11_sym(l, "XConnectionNumber", &x.ConnectionNumber) && fire_dx11_sym(l, "XSelectInput", &x.SelectInput) && fire_dx11_sym(l, "XCreateGC", &x.CreateGC)
            && fire_dx11_sym(l, "XFreeGC", &x.FreeGC) && fire_dx11_sym(l, "XCreateImage", &x.CreateImage) && fire_dx11_sym(l, "XPutImage", &x.PutImage)
            && fire_dx11_sym(l, "XCreateFontCursor", &x.CreateFontCursor) && fire_dx11_sym(l, "XDefineCursor", &x.DefineCursor) && fire_dx11_sym(l, "XFreeCursor", &x.FreeCursor)
            && fire_dx11_sym(l, "XLookupKeysym", &x.LookupKeysym) && fire_dx11_sym(l, "XFilterEvent", &x.FilterEvent) && fire_dx11_sym(l, "XGetEventData", &x.GetEventData)
            && fire_dx11_sym(l, "XFreeEventData", &x.FreeEventData) && fire_dx11_sym(l, "XQueryExtension", &x.QueryExtension)
            && fire_dx11_sym(l, "XGetSelectionOwner", &x.GetSelectionOwner) && fire_dx11_sym(l, "XSetSelectionOwner", &x.SetSelectionOwner)
            && fire_dx11_sym(l, "XConvertSelection", &x.ConvertSelection) && fire_dx11_sym(l, "XTranslateCoordinates", &x.TranslateCoordinates)
            && fire_dx11_sym(l, "XExtendedMaxRequestSize", &x.ExtendedMaxRequestSize) && fire_dx11_sym(l, "XMaxRequestSize", &x.MaxRequestSize)
            && fire_dx11_sym(l, "XResourceManagerString", &x.ResourceManagerString) && fire_dx11_sym(l, "XSetErrorHandler", &x.SetErrorHandler)
            && fire_dx11_sym(l, "XRefreshKeyboardMapping", &x.RefreshKeyboardMapping);
        if (x.ok) {
            /* the input method is optional (a program without it still gets the text of the keys of the layout) */
            if (!(fire_dx11_sym(l, "XSupportsLocale", &x.SupportsLocale) && fire_dx11_sym(l, "XSetLocaleModifiers", &x.SetLocaleModifiers) && fire_dx11_sym(l, "XOpenIM", &x.OpenIM)
                  && fire_dx11_sym(l, "XCloseIM", &x.CloseIM) && fire_dx11_sym(l, "XGetIMValues", &x.GetIMValues) && fire_dx11_sym(l, "XCreateIC", &x.CreateIC)
                  && fire_dx11_sym(l, "XDestroyIC", &x.DestroyIC) && fire_dx11_sym(l, "XSetICFocus", &x.SetICFocus) && fire_dx11_sym(l, "XUnsetICFocus", &x.UnsetICFocus)
                  && fire_dx11_sym(l, "XSetICValues", &x.SetICValues) && fire_dx11_sym(l, "XVaCreateNestedList", &x.VaCreateNestedList)
                  && fire_dx11_sym(l, "Xutf8LookupString", &x.Utf8LookupString) && fire_dx11_sym(l, "Xutf8ResetIC", &x.Utf8ResetIC)))
                x.OpenIM = NULL;
            x.ext = fire_dx11_open2("libXext.so.6", "libXext.so");
            if (!(fire_dx11_sym(x.ext, "XShmQueryExtension", &x.ShmQueryExtension) && fire_dx11_sym(x.ext, "XShmCreateImage", &x.ShmCreateImage)
                  && fire_dx11_sym(x.ext, "XShmAttach", &x.ShmAttach) && fire_dx11_sym(x.ext, "XShmDetach", &x.ShmDetach) && fire_dx11_sym(x.ext, "XShmPutImage", &x.ShmPutImage)))
                x.ShmQueryExtension = NULL;
            x.xi = fire_dx11_open2("libXi.so.6", "libXi.so");
            if (!(fire_dx11_sym(x.xi, "XIQueryVersion", &x.XIQueryVersion) && fire_dx11_sym(x.xi, "XISelectEvents", &x.XISelectEvents))) x.XISelectEvents = NULL;
            x.xcursor = fire_dx11_open2("libXcursor.so.1", "libXcursor.so");
            fire_dx11_sym(x.xcursor, "XcursorLibraryLoadCursor", &x.CursorLibraryLoadCursor);
            /* (several threads talk to X - every window on its own connection, the clipboard on another): libX11 must know before anything else */
            x.InitThreads();
        }
    }
    pthread_mutex_unlock(&guard);
    return x.ok ? &x : NULL;
}

/* Can a window be opened on X11 (libX11 is there and the server named by DISPLAY answers)? */
static int fire_dx11_available(void) {
    FireXlib* x;
    void* d;
    const char* name = getenv("DISPLAY");
    if (name == NULL || name[0] == 0 || (x = fire_dx11_lib()) == NULL) return 0;
    d = x->OpenDisplay(NULL);
    if (d == NULL) return 0;
    x->CloseDisplay(d);
    return 1;
}

/* (an error of the server - a window of another program that went away, a request a server does not have - is noted instead of ending the program) */
typedef struct FireXErrorEvent { int type; void* display; unsigned long resourceid, serial; unsigned char error_code, request_code, minor_code; } FireXErrorEvent;
static volatile int fire_dx11_lastError = 0;
static int fire_dx11_onError(void* display, void* error) { (void)display; fire_dx11_lastError = error != NULL ? ((FireXErrorEvent*)error)->error_code : 1; return 0; }

/* ---- time: a frame with VSync waits until 1/60 s since the last one passed ---- */
static double fire_dx11_now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + (double)t.tv_nsec * 1e-9; }
static void fire_dx11_pace(double* last) {
    double now = fire_dx11_now(), next = *last + 1.0 / 60.0;
    if (*last > 0 && now < next) {
        struct timespec s;
        double wait = next - now;
        s.tv_sec = (time_t)wait;
        s.tv_nsec = (long)((wait - (double)s.tv_sec) * 1e9);
#ifdef FIRE_RT_HELPING_WAIT
        if (!fire_frameWait(wait))   /* (the main program of a native build: its thread runs tasks meanwhile) */
#endif
        nanosleep(&s, NULL);
        *last = next;
    } else *last = now;
}

/* ---- the atoms ---- */
typedef struct FireDx11Atoms {
    FireXId wmProtocols, wmDelete, netWmName, netWmPid, utf8, clipboard, targets, incr, textPlainUtf8, textPlain, string, text, png, bmp, uriList, gnomeFiles, property,
        xdndAware, xdndEnter, xdndPosition, xdndStatus, xdndLeave, xdndDrop, xdndFinished, xdndSelection, xdndTypeList, xdndActionCopy, cm, kdeBlur, netSupported, atom, cardinal;
} FireDx11Atoms;
static void fire_dx11_atoms(FireXlib* x, void* d, FireDx11Atoms* a) {
    a->wmProtocols = x->InternAtom(d, "WM_PROTOCOLS", 0);
    a->wmDelete = x->InternAtom(d, "WM_DELETE_WINDOW", 0);
    a->netWmName = x->InternAtom(d, "_NET_WM_NAME", 0);
    a->netWmPid = x->InternAtom(d, "_NET_WM_PID", 0);
    a->utf8 = x->InternAtom(d, "UTF8_STRING", 0);
    a->clipboard = x->InternAtom(d, "CLIPBOARD", 0);
    a->targets = x->InternAtom(d, "TARGETS", 0);
    a->incr = x->InternAtom(d, "INCR", 0);
    a->textPlainUtf8 = x->InternAtom(d, "text/plain;charset=utf-8", 0);
    a->textPlain = x->InternAtom(d, "text/plain", 0);
    a->string = 31;   /* XA_STRING */
    a->text = x->InternAtom(d, "TEXT", 0);
    a->png = x->InternAtom(d, "image/png", 0);
    a->bmp = x->InternAtom(d, "image/bmp", 0);
    a->uriList = x->InternAtom(d, "text/uri-list", 0);
    a->gnomeFiles = x->InternAtom(d, "x-special/gnome-copied-files", 0);
    a->property = x->InternAtom(d, "FIRE_SELECTION", 0);
    a->xdndAware = x->InternAtom(d, "XdndAware", 0);
    a->xdndEnter = x->InternAtom(d, "XdndEnter", 0);
    a->xdndPosition = x->InternAtom(d, "XdndPosition", 0);
    a->xdndStatus = x->InternAtom(d, "XdndStatus", 0);
    a->xdndLeave = x->InternAtom(d, "XdndLeave", 0);
    a->xdndDrop = x->InternAtom(d, "XdndDrop", 0);
    a->xdndFinished = x->InternAtom(d, "XdndFinished", 0);
    a->xdndSelection = x->InternAtom(d, "XdndSelection", 0);
    a->xdndTypeList = x->InternAtom(d, "XdndTypeList", 0);
    a->xdndActionCopy = x->InternAtom(d, "XdndActionCopy", 0);
    a->cm = x->InternAtom(d, "_NET_WM_CM_S0", 0);
    a->kdeBlur = x->InternAtom(d, "_KDE_NET_WM_BLUR_BEHIND_REGION", 0);
    a->netSupported = x->InternAtom(d, "_NET_SUPPORTED", 0);
    a->atom = 4;       /* XA_ATOM */
    a->cardinal = 6;   /* XA_CARDINAL */
}

/* Reads a whole property (malloc'd with a terminating 0; *n bytes, *type its type; NULL: none). `del`: deletes it after reading. */
static unsigned char* fire_dx11_readProperty(FireXlib* x, void* d, FireXId w, FireXId prop, int del, FireXId* type, size_t* n) {
    FireXId actual = 0;
    int format = 0;
    unsigned long items = 0, after = 0;
    unsigned char* data = NULL;
    unsigned char* out;
    size_t bytes;
    *n = 0;
    if (type) *type = 0;
    if (x->GetWindowProperty(d, w, prop, 0, 0x7FFFFFFF / 4, del, 0 /* AnyPropertyType */, &actual, &format, &items, &after, &data) != 0 || actual == 0) {
        if (data) x->Free(data);
        return NULL;
    }
    bytes = format == 32 ? items * sizeof(long) : format == 16 ? items * 2 : items;
    out = (unsigned char*)malloc(bytes + 1);
    if (out != NULL) { if (bytes > 0) memcpy(out, data, bytes); out[bytes] = 0; *n = bytes; }
    if (type) *type = actual;
    if (data) x->Free(data);
    return out;
}

/* ================================================================================================================================================
 * The clipboard: a connection of its own with a hidden window, served by a thread (the owner of a selection must answer whenever another program asks).
 * ================================================================================================================================================ */
typedef struct FireDx11Clip {
    int started, failed;
    pthread_mutex_t m;
    void* d;
    FireXId win;
    FireDx11Atoms a;
    long maxChunk;
    /* what this program offers (while it owns CLIPBOARD) */
    int owner;
    char* text;
    unsigned char* png; size_t pngSize;
    unsigned char* bmp; size_t bmpSize;
    char* uris;
    char* gnome;
    /* answers being sent in pieces (INCR) to other programs */
    struct FireDx11Incr { FireXId requestor, property, type; unsigned char* data; size_t size, pos; } incr[8];
    /* a question of ours to the owner: its answer */
    FireXId waitProp;
    int answered;
    FireXId answerType;
    unsigned char* answer; size_t answerSize;
    int incrReading;
} FireDx11Clip;
static FireDx11Clip g_fireDx11Clip = { 0, 0, PTHREAD_MUTEX_INITIALIZER, NULL, 0, { 0 }, 0, 0, NULL, NULL, 0, NULL, 0, NULL, NULL, { { 0, 0, 0, NULL, 0, 0 } }, 0, 0, 0, NULL, 0, 0 };

static void fire_dx11_clipForget(FireDx11Clip* c) {
    free(c->text); free(c->png); free(c->bmp); free(c->uris); free(c->gnome);
    c->text = NULL; c->png = c->bmp = NULL; c->uris = c->gnome = NULL; c->pngSize = c->bmpSize = 0;
    c->owner = 0;
}

/* What this program offers as `target` (NULL: nothing). */
static const unsigned char* fire_dx11_clipData(FireDx11Clip* c, FireXId target, size_t* n, FireXId* type) {
    FireDx11Atoms* a = &c->a;
    *type = target;
    if (c->text && (target == a->utf8 || target == a->textPlainUtf8 || target == a->string || target == a->text || target == a->textPlain)) {
        if (target == a->text) *type = a->utf8;
        *n = strlen(c->text);
        return (const unsigned char*)c->text;
    }
    if (c->png && target == a->png) { *n = c->pngSize; return c->png; }
    if (c->bmp && target == a->bmp) { *n = c->bmpSize; return c->bmp; }
    if (c->uris && target == a->uriList) { *n = strlen(c->uris); return (const unsigned char*)c->uris; }
    if (c->gnome && target == a->gnomeFiles) { *n = strlen(c->gnome); return (const unsigned char*)c->gnome; }
    return NULL;
}

/* Another program asks for the clipboard (the caller holds the lock). */
static void fire_dx11_clipRequest(FireXlib* x, FireDx11Clip* c, const FireXSelRequest* r) {
    FireXEv reply;
    FireXId prop = r->property != 0 ? r->property : r->target;
    int ok = 0;
    memset(&reply, 0, sizeof reply);
    reply.xselection.type = FIRE_X_SELECTION_NOTIFY;
    reply.xselection.display = c->d;
    reply.xselection.requestor = r->requestor;
    reply.xselection.selection = r->selection;
    reply.xselection.target = r->target;
    reply.xselection.time = r->time;
    if (c->owner && r->selection == c->a.clipboard) {
        if (r->target == c->a.targets) {
            FireXId list[12];
            int n = 0;
            list[n++] = c->a.targets;
            if (c->text) { list[n++] = c->a.utf8; list[n++] = c->a.textPlainUtf8; list[n++] = c->a.string; list[n++] = c->a.text; list[n++] = c->a.textPlain; }
            if (c->png) list[n++] = c->a.png;
            if (c->bmp) list[n++] = c->a.bmp;
            if (c->uris) { list[n++] = c->a.uriList; list[n++] = c->a.gnomeFiles; }
            x->ChangeProperty(c->d, r->requestor, prop, c->a.atom, 32, 0 /* PropModeReplace */, (const unsigned char*)list, n);
            ok = 1;
        } else {
            size_t n = 0;
            FireXId type = 0;
            const unsigned char* data = fire_dx11_clipData(c, r->target, &n, &type);
            if (data != NULL) {
                if ((long)n > c->maxChunk) {
                    /* too large for one request: in pieces - INCR with the size, then a piece whenever the requestor deleted the last one */
                    int k;
                    for (k = 0; k < 8; k++) if (c->incr[k].data == NULL) break;
                    if (k < 8) {
                        unsigned char* copy = (unsigned char*)malloc(n);
                        if (copy != NULL) {
                            long size = (long)n;
                            memcpy(copy, data, n);
                            c->incr[k].requestor = r->requestor; c->incr[k].property = prop; c->incr[k].type = type;
                            c->incr[k].data = copy; c->incr[k].size = n; c->incr[k].pos = 0;
                            x->SelectInput(c->d, r->requestor, 0x400000 /* PropertyChangeMask */);
                            x->ChangeProperty(c->d, r->requestor, prop, c->a.incr, 32, 0, (const unsigned char*)&size, 1);
                            ok = 1;
                        }
                    }
                } else {
                    x->ChangeProperty(c->d, r->requestor, prop, type, 8, 0, data, (int)n);
                    ok = 1;
                }
            }
        }
    }
    reply.xselection.property = ok ? prop : 0;
    x->SendEvent(c->d, r->requestor, 0, 0, &reply);
    x->Flush(c->d);
}

/* One event of the clipboard's connection (the caller holds the lock). */
static void fire_dx11_clipEvent(FireXlib* x, FireDx11Clip* c, FireXEv* e) {
    switch (e->type) {
        case FIRE_X_SELECTION_REQUEST: fire_dx11_clipRequest(x, c, &e->xselectionrequest); break;
        case FIRE_X_SELECTION_CLEAR:
            if (e->xselectionclear.selection == c->a.clipboard) fire_dx11_clipForget(c);
            break;
        case FIRE_X_SELECTION_NOTIFY:
            if (e->xselection.requestor == c->win && c->waitProp != 0 && !c->answered) {
                if (e->xselection.property == 0) { c->answered = 1; break; }
                {
                    size_t n = 0;
                    FireXId type = 0;
                    unsigned char* data = fire_dx11_readProperty(x, c->d, c->win, e->xselection.property, 1, &type, &n);
                    if (type == c->a.incr) { free(data); c->incrReading = 1; c->answerType = 0; break; }   /* the pieces follow as PropertyNotify */
                    c->answer = data; c->answerSize = n; c->answerType = type; c->answered = 1;
                }
            }
            break;
        case FIRE_X_PROPERTY_NOTIFY: {
            int k;
            if (e->xproperty.window == c->win && c->incrReading && e->xproperty.state == 0 /* PropertyNewValue */ && e->xproperty.atom == c->waitProp) {
                size_t n = 0;
                FireXId type = 0;
                unsigned char* piece = fire_dx11_readProperty(x, c->d, c->win, c->waitProp, 1, &type, &n);
                if (n == 0) { free(piece); c->incrReading = 0; c->answered = 1; break; }   /* (an empty piece ends it) */
                if (piece != NULL) {
                    unsigned char* grown = (unsigned char*)realloc(c->answer, c->answerSize + n + 1);
                    if (grown != NULL) { memcpy(grown + c->answerSize, piece, n); c->answerSize += n; grown[c->answerSize] = 0; c->answer = grown; c->answerType = type; }
                    free(piece);
                }
                break;
            }
            for (k = 0; k < 8; k++) {
                struct FireDx11Incr* in = &c->incr[k];
                if (in->data != NULL && e->xproperty.window == in->requestor && e->xproperty.atom == in->property && e->xproperty.state == 1 /* PropertyDelete */) {
                    size_t left = in->size - in->pos, piece = left > (size_t)c->maxChunk ? (size_t)c->maxChunk : left;
                    x->ChangeProperty(c->d, in->requestor, in->property, in->type, 8, 0, in->data + in->pos, (int)piece);
                    in->pos += piece;
                    if (piece == 0) { free(in->data); memset(in, 0, sizeof *in); }   /* (the empty piece was the last) */
                    x->Flush(c->d);
                }
            }
            break;
        }
        default: break;
    }
}

/* Takes in what came on the clipboard's connection (the caller holds the lock). */
static void fire_dx11_clipDrain(FireXlib* x, FireDx11Clip* c) {
    while (x->Pending(c->d) > 0) {
        FireXEv e;
        x->NextEvent(c->d, &e);
        fire_dx11_clipEvent(x, c, &e);
    }
}

static void* fire_dx11_clipThread(void* arg) {
    FireDx11Clip* c = (FireDx11Clip*)arg;
    FireXlib* x = fire_dx11_lib();
    struct pollfd p;
    p.fd = x->ConnectionNumber(c->d);
    p.events = POLLIN;
    for (;;) {
        p.revents = 0;
        poll(&p, 1, 200);
        pthread_mutex_lock(&c->m);
        fire_dx11_clipDrain(x, c);
        pthread_mutex_unlock(&c->m);
    }
    return NULL;
}

/* The clipboard's connection, made once (NULL: no X server). The caller holds the lock. */
static FireDx11Clip* fire_dx11_clipReady(void) {
    FireDx11Clip* c = &g_fireDx11Clip;
    FireXlib* x = fire_dx11_lib();
    if (x == NULL) return NULL;
    if (!c->started && !c->failed) {
        FireXSetAttributes attr;
        pthread_t thread;
        c->d = x->OpenDisplay(NULL);
        if (c->d == NULL) { c->failed = 1; return NULL; }
        fire_dx11_atoms(x, c->d, &c->a);
        memset(&attr, 0, sizeof attr);
        attr.event_mask = 0x400000;   /* PropertyChangeMask */
        c->win = x->CreateWindow(c->d, x->RootWindow(c->d, x->DefaultScreen(c->d)), -10, -10, 1, 1, 0, 0 /* CopyFromParent */, 2 /* InputOnly */, NULL, 0x800 /* CWEventMask */, &attr);
        c->maxChunk = x->ExtendedMaxRequestSize(c->d);
        if (c->maxChunk <= 0) c->maxChunk = x->MaxRequestSize(c->d);
        c->maxChunk = c->maxChunk * 4 - 256;
        if (c->maxChunk > 1 << 20) c->maxChunk = 1 << 20;
        if (c->maxChunk < 4096) c->maxChunk = 4096;
        x->Flush(c->d);
        c->started = 1;
        if (pthread_create(&thread, NULL, fire_dx11_clipThread, c) == 0) pthread_detach(thread);
    }
    return c->started ? c : NULL;
}

/* Asks the owner of CLIPBOARD for `target` and waits for the answer (at most a second); malloc'd bytes with a terminating 0, or NULL. The caller holds the lock. */
static unsigned char* fire_dx11_clipAsk(FireXlib* x, FireDx11Clip* c, FireXId target, size_t* n, FireXId* type) {
    double until = fire_dx11_now() + 1.0;
    struct pollfd p;
    unsigned char* got;
    *n = 0;
    if (type) *type = 0;
    if (x->GetSelectionOwner(c->d, c->a.clipboard) == 0) return NULL;
    c->waitProp = c->a.property;
    c->answered = 0; c->incrReading = 0;
    free(c->answer); c->answer = NULL; c->answerSize = 0; c->answerType = 0;
    x->DeleteProperty(c->d, c->win, c->a.property);
    x->ConvertSelection(c->d, c->a.clipboard, target, c->a.property, c->win, 0 /* CurrentTime */);
    x->Flush(c->d);
    p.fd = x->ConnectionNumber(c->d);
    p.events = POLLIN;
    while (!c->answered && fire_dx11_now() < until) {
        fire_dx11_clipDrain(x, c);
        if (c->answered) break;
        p.revents = 0;
        poll(&p, 1, 10);
    }
    got = c->answered ? c->answer : NULL;
    if (got != NULL) { *n = c->answerSize; if (type) *type = c->answerType; } else free(c->answer);
    c->answer = NULL; c->answerSize = 0;
    c->waitProp = 0;
    c->incrReading = 0;
    return got;
}

/* Does the owner of the clipboard offer `target`? */
static int fire_dx11_clipOffers(FireXlib* x, FireDx11Clip* c, FireXId target) {
    size_t n = 0, i;
    FireXId type = 0;
    unsigned char* list;
    if (c->owner) { FireXId t; return fire_dx11_clipData(c, target, &n, &t) != NULL; }
    list = fire_dx11_clipAsk(x, c, c->a.targets, &n, &type);
    if (list == NULL) return 0;
    for (i = 0; i + sizeof(long) <= n; i += sizeof(long)) if ((FireXId)((long*)(void*)(list + i))[0] == target) { free(list); return 1; }
    free(list);
    return 0;
}

/* Becomes the owner of CLIPBOARD with what is in `c` (the caller holds the lock and filled it). */
static int fire_dx11_clipOwn(FireXlib* x, FireDx11Clip* c) {
    x->SetSelectionOwner(c->d, c->a.clipboard, c->win, 0);
    x->Flush(c->d);
    c->owner = x->GetSelectionOwner(c->d, c->a.clipboard) == c->win;
    if (!c->owner) fire_dx11_clipForget(c);
    return c->owner;
}

static int fire_dx11_clipboardSet(const char* text) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    int ok = 0;
    if (x == NULL || text == NULL) return 0;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) {
        fire_dx11_clipForget(c);
        c->text = (char*)fire_display_copyBytes(text, strlen(text));
        ok = c->text != NULL && fire_dx11_clipOwn(x, c);
    }
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return ok;
}

static char* fire_dx11_clipboardGet(void) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    char* text = NULL;
    if (x == NULL) return NULL;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) {
        if (c->owner) { if (c->text && c->text[0]) text = (char*)fire_display_copyBytes(c->text, strlen(c->text)); }
        else {
            size_t n = 0;
            text = (char*)fire_dx11_clipAsk(x, c, c->a.utf8, &n, NULL);
            if (text == NULL) text = (char*)fire_dx11_clipAsk(x, c, c->a.textPlainUtf8, &n, NULL);
            if (text == NULL) text = (char*)fire_dx11_clipAsk(x, c, c->a.string, &n, NULL);
            if (text != NULL && text[0] == 0) { free(text); text = NULL; }
        }
    }
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return text;
}

static int fire_dx11_clipboardHas(void) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    int has = 0;
    if (x == NULL) return 0;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) has = c->owner ? (c->text != NULL && c->text[0] != 0) : fire_dx11_clipOffers(x, c, c->a.utf8) || fire_dx11_clipOffers(x, c, c->a.string);
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return has;
}

static unsigned char* fire_dx11_clipboardGetImage(size_t* size) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    unsigned char* image = NULL;
    *size = 0;
    if (x == NULL) return NULL;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) {
        if (c->owner) {
            if (c->png) { image = fire_display_copyBytes(c->png, c->pngSize); if (image) *size = c->pngSize; }
        } else {
            image = fire_dx11_clipAsk(x, c, c->a.png, size, NULL);
            if (image == NULL) image = fire_dx11_clipAsk(x, c, c->a.bmp, size, NULL);
            if (image != NULL && *size == 0) { free(image); image = NULL; }
        }
    }
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return image;
}

static int fire_dx11_clipboardHasImage(void) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    int has = 0;
    if (x == NULL) return 0;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) has = c->owner ? c->png != NULL : fire_dx11_clipOffers(x, c, c->a.png) || fire_dx11_clipOffers(x, c, c->a.bmp);
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return has;
}

static int fire_dx11_clipboardSetImage(const unsigned char* png, size_t pngSize, const unsigned char* bmp, size_t bmpSize) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    int ok = 0;
    if (x == NULL || png == NULL || pngSize == 0) return 0;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) {
        fire_dx11_clipForget(c);
        c->png = fire_display_copyBytes(png, pngSize); c->pngSize = pngSize;
        if (bmp != NULL && bmpSize > 0) { c->bmp = fire_display_copyBytes(bmp, bmpSize); c->bmpSize = bmpSize; }
        ok = c->png != NULL && fire_dx11_clipOwn(x, c);
    }
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return ok;
}

static char* fire_dx11_clipboardGetFiles(void) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    char* paths = NULL;
    if (x == NULL) return NULL;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) {
        size_t n = 0;
        unsigned char* list;
        if (c->owner) list = c->uris ? fire_display_copyBytes(c->uris, n = strlen(c->uris)) : NULL;
        else {
            list = fire_dx11_clipAsk(x, c, c->a.uriList, &n, NULL);
            if (list == NULL) list = fire_dx11_clipAsk(x, c, c->a.gnomeFiles, &n, NULL);
        }
        if (list != NULL) { paths = fire_display_pathsFromUriList((const char*)list, n); free(list); }
    }
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return paths;
}

static int fire_dx11_clipboardHasFiles(void) { char* p = fire_dx11_clipboardGetFiles(); int has = p != NULL; free(p); return has; }

static int fire_dx11_clipboardSetFiles(const char* paths) {
    FireXlib* x = fire_dx11_lib();
    FireDx11Clip* c;
    int ok = 0;
    if (x == NULL || paths == NULL || paths[0] == 0) return 0;
    pthread_mutex_lock(&g_fireDx11Clip.m);
    if ((c = fire_dx11_clipReady()) != NULL) {
        fire_dx11_clipForget(c);
        c->uris = fire_display_uriListFromPaths(paths);
        if (c->uris != NULL) {
            size_t n = strlen(c->uris);
            c->gnome = (char*)malloc(n + 6);
            if (c->gnome != NULL) { memcpy(c->gnome, "copy\n", 5); memcpy(c->gnome + 5, c->uris, n + 1); }
        }
        ok = c->uris != NULL && c->gnome != NULL && fire_dx11_clipOwn(x, c);
    }
    pthread_mutex_unlock(&g_fireDx11Clip.m);
    return ok;
}

/* ================================================================================================================================================
 * A window
 * ================================================================================================================================================ */
typedef struct FireDx11Window {
    FireXlib* x;
    void* d;
    int screen;
    FireXId win, root, colormap;
    void* visual;
    int depth, argb;
    void* gc;
    FireDx11Atoms a;
    int winW, winH;
    int vsync, touchMouse, cursor, closed, direct;
    FireXId cursors[FIRE_CURSOR_COUNT];
    char* title;
    double lastFrame;
    double scale;
    /* what is shown: the pixels of the window (and their image); MIT-SHM when the server is local */
    FireXImage* image;
    FireXShmInfo shm;
    int useShm, shmTried;
    int imgW, imgH;
    uint32_t* pixels;   /* (without MIT-SHM: the memory of the image) */
    int backdrop;
    /* the input method */
    void* im;
    void* ic;
    unsigned long imStyle;
    uint32_t* preedit; int preeditLen, preeditCap, preeditCaret;
    int composing;
    int textX, textY, textW, textH;
    int mods;   /* the modifier keys that are down (fire_display_keys.h bits) */
    int repeatNext;   /* the next press is the auto-repeat of the key (its release was left out) */
    /* fingers (XInput 2.2) */
    int xiOpcode, touch;
    int touchMouseFinger;   /* the finger that moves the mouse (-1: none) */
    /* XDND: what is dragged over the window */
    FireXId dndSource, dndType;
    int dndVersion;
    float dndX, dndY;
    FireDisplayQueue queue;
} FireDx11Window;

static void fire_dx11_threadDone(void) {}

static void fire_dx11_init(FireDx11Window* w) {
    memset(w, 0, sizeof *w);
    w->vsync = 1;
    w->touchMouse = 1;
    w->cursor = -1;
    w->touchMouseFinger = -1;
    w->scale = 1.0;
}

/* Is a compositor running (only then can a window be transparent)? */
static int fire_dx11_compositor(FireDx11Window* w) { return w->x->GetSelectionOwner(w->d, w->a.cm) != 0; }

/* The scale of the desktop: Xft.dpi / 96 (as the toolkits read it), else 1. */
static double fire_dx11_readScale(FireXlib* x, void* d) {
    char* rm = x->ResourceManagerString(d);
    const char* p;
    if (rm == NULL) return 1.0;
    p = strstr(rm, "Xft.dpi:");
    if (p != NULL) {
        double dpi = atof(p + 8);
        if (dpi >= 48 && dpi <= 960) return dpi / 96.0;
    }
    return 1.0;
}

/* ---- the input method (XIM) ---- */
static int fire_dx11_preeditStart(void* ic, char* client, char* call) { (void)ic; (void)call; ((FireDx11Window*)(void*)client)->composing = 1; return -1; }
static void fire_dx11_preeditSend(FireDx11Window* w) {
    /* the composition as UTF-8, its cursor in characters */
    char* text = (char*)malloc((size_t)w->preeditLen * 4 + 1);
    int k = 0, i;
    if (text == NULL) return;
    for (i = 0; i < w->preeditLen; i++) k += fire_display_utf8(w->preedit[i], text + k);
    text[k] = 0;
    fire_display_queueEditing(&w->queue, text, (size_t)k, w->preeditCaret, 0);
    free(text);
}
static void fire_dx11_preeditDone(void* ic, char* client, char* call) {
    FireDx11Window* w = (FireDx11Window*)(void*)client;
    (void)ic; (void)call;
    w->preeditLen = 0; w->preeditCaret = 0;
    if (w->composing) fire_display_queueEditing(&w->queue, NULL, 0, 0, 0);
    w->composing = 0;
}
static void fire_dx11_preeditDraw(void* ic, char* client, char* call) {
    FireDx11Window* w = (FireDx11Window*)(void*)client;
    FireXIMDraw* draw = (FireXIMDraw*)(void*)call;
    uint32_t cps[256];
    int count = 0, first, length, newLen, i;
    (void)ic;
    if (draw == NULL) return;
    /* the new characters (the multi-byte string is UTF-8: the program runs in a UTF-8 locale for the input method) */
    if (draw->text != NULL && !draw->text->encoding_is_wchar && draw->text->string.multi_byte != NULL) {
        const unsigned char* s = (const unsigned char*)draw->text->string.multi_byte;
        while (*s && count < 256) {
            uint32_t c = *s++;
            int more = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
            if (more) c &= 0x3Fu >> more;
            while (more-- > 0 && (*s & 0xC0) == 0x80) c = (c << 6) | (*s++ & 0x3F);
            cps[count++] = c;
        }
    }
    first = draw->chg_first < 0 ? 0 : draw->chg_first > w->preeditLen ? w->preeditLen : draw->chg_first;
    length = draw->chg_length < 0 ? 0 : first + draw->chg_length > w->preeditLen ? w->preeditLen - first : draw->chg_length;
    newLen = w->preeditLen - length + count;
    if (newLen > w->preeditCap) {
        uint32_t* grown = (uint32_t*)realloc(w->preedit, sizeof(uint32_t) * (size_t)(newLen + 16));
        if (grown == NULL) return;
        w->preedit = grown; w->preeditCap = newLen + 16;
    }
    memmove(w->preedit + first + count, w->preedit + first + length, sizeof(uint32_t) * (size_t)(w->preeditLen - first - length));
    for (i = 0; i < count; i++) w->preedit[first + i] = cps[i];
    w->preeditLen = newLen;
    w->preeditCaret = draw->caret < 0 ? 0 : draw->caret > newLen ? newLen : draw->caret;
    w->composing = 1;
    fire_dx11_preeditSend(w);
}
static void fire_dx11_preeditCaret(void* ic, char* client, char* call) {
    FireDx11Window* w = (FireDx11Window*)(void*)client;
    FireXIMCaret* caret = (FireXIMCaret*)(void*)call;
    (void)ic;
    if (caret == NULL) return;
    if (caret->direction == 10 /* XIMAbsolutePosition */) w->preeditCaret = caret->position;
    else if (caret->direction == 0 /* XIMForwardChar */) w->preeditCaret++;
    else if (caret->direction == 1 /* XIMBackwardChar */) w->preeditCaret--;
    else if (caret->direction == 4 /* XIMLineStart */) w->preeditCaret = 0;
    else if (caret->direction == 5 /* XIMLineEnd */) w->preeditCaret = w->preeditLen;
    if (w->preeditCaret < 0) w->preeditCaret = 0;
    if (w->preeditCaret > w->preeditLen) w->preeditCaret = w->preeditLen;
    caret->position = w->preeditCaret;
    if (w->composing) fire_dx11_preeditSend(w);
}

static void fire_dx11_openIM(FireDx11Window* w) {
    FireXlib* x = w->x;
    FireXIMStyles* styles = NULL;
    unsigned long want = 0;
    unsigned short i;
    if (x->OpenIM == NULL) return;
    /* the input method speaks the encoding of the locale: UTF-8 (only LC_CTYPE - numbers keep being printed as before) */
    {
        const char* ctype = setlocale(LC_CTYPE, NULL);
        if (ctype == NULL || strcmp(ctype, "C") == 0 || strcmp(ctype, "POSIX") == 0) {
            if (setlocale(LC_CTYPE, "") == NULL || !x->SupportsLocale()) setlocale(LC_CTYPE, "C.UTF-8");
        }
    }
    if (!x->SupportsLocale()) return;
    x->SetLocaleModifiers("");
    w->im = x->OpenIM(w->d, NULL, NULL, NULL);
    if (w->im == NULL) { x->SetLocaleModifiers("@im=none"); w->im = x->OpenIM(w->d, NULL, NULL, NULL); }
    if (w->im == NULL) return;
    if (x->GetIMValues(w->im, "queryInputStyle", &styles, (char*)NULL) == NULL && styles != NULL) {
        /* the composition drawn by the program (callbacks), else at the spot it names, else wherever the input method likes */
        static const unsigned long prefer[] = { 0x2 | 0x400, 0x4 | 0x400, 0x8 | 0x400, 0x10 | 0x800 };
        int p;
        for (p = 0; p < 4 && want == 0; p++)
            for (i = 0; i < styles->count; i++) if (styles->styles[i] == prefer[p]) { want = prefer[p]; break; }
        x->Free(styles);
    }
    if (want == 0) want = 0x8 | 0x400;   /* XIMPreeditNothing | XIMStatusNothing */
    if (want & 0x2) {
        /* (the input method keeps copies of the callbacks; their data is this window) */
        FireXIMCallback start, done, draw, caret;
        void* list;
        int (*startFn)(void*, char*, char*) = fire_dx11_preeditStart;
        start.client_data = (char*)(void*)w; memcpy(&start.callback, &startFn, sizeof startFn);
        done.client_data = (char*)(void*)w; done.callback = fire_dx11_preeditDone;
        draw.client_data = (char*)(void*)w; draw.callback = fire_dx11_preeditDraw;
        caret.client_data = (char*)(void*)w; caret.callback = fire_dx11_preeditCaret;
        list = x->VaCreateNestedList(0, "preeditStartCallback", &start, "preeditDoneCallback", &done, "preeditDrawCallback", &draw, "preeditCaretCallback", &caret, (char*)NULL);
        w->ic = x->CreateIC(w->im, "inputStyle", want, "clientWindow", w->win, "focusWindow", w->win, "preeditAttributes", list, (char*)NULL);
        if (list) x->Free(list);
    } else if (want & 0x4) {
        FireXPoint spot;
        void* list;
        spot.x = 0; spot.y = 0;
        list = x->VaCreateNestedList(0, "spotLocation", &spot, (char*)NULL);
        w->ic = x->CreateIC(w->im, "inputStyle", want, "clientWindow", w->win, "focusWindow", w->win, "preeditAttributes", list, (char*)NULL);
        if (list) x->Free(list);
    } else {
        w->ic = x->CreateIC(w->im, "inputStyle", want, "clientWindow", w->win, "focusWindow", w->win, (char*)NULL);
    }
    w->imStyle = want;
    if (w->ic == NULL) { x->CloseIM(w->im); w->im = NULL; }
}

/* ---- the window ---- */
static void fire_dx11_freeImage(FireDx11Window* w) {
    FireXlib* x = w->x;
    if (w->image != NULL) {
        if (w->useShm) {
            x->ShmDetach(w->d, &w->shm);
            x->Sync(w->d, 0);
            shmdt(w->shm.shmaddr);
            w->image->data = NULL;
        } else w->image->data = NULL;
        if (w->image->f.destroy_image) w->image->f.destroy_image(w->image);
        w->image = NULL;
    }
    free(w->pixels);
    w->pixels = NULL;
    w->imgW = w->imgH = 0;
}

static int fire_dx11_makeImage(FireDx11Window* w, int width, int height) {
    FireXlib* x = w->x;
    fire_dx11_freeImage(w);
    if (!w->shmTried) {
        w->shmTried = 1;
        w->useShm = x->ShmQueryExtension != NULL && x->ShmQueryExtension(w->d);
        /* (a server on another machine cannot see our memory) */
        { const char* name = getenv("DISPLAY"); if (name != NULL && name[0] != ':' && strncmp(name, "unix:", 5) != 0) w->useShm = 0; }
    }
    if (w->useShm) {
        FireXErrorHandler old;
        w->image = x->ShmCreateImage(w->d, w->visual, (unsigned)w->depth, 2 /* ZPixmap */, NULL, &w->shm, (unsigned)width, (unsigned)height);
        if (w->image != NULL) {
            w->shm.shmid = shmget(IPC_PRIVATE, (size_t)w->image->bytes_per_line * (size_t)height, IPC_CREAT | 0600);
            w->shm.shmaddr = w->shm.shmid >= 0 ? (char*)shmat(w->shm.shmid, NULL, 0) : (char*)-1;
            if (w->shm.shmaddr != (char*)-1) {
                w->image->data = w->shm.shmaddr;
                w->shm.readOnly = 0;
                fire_dx11_lastError = 0;
                old = x->SetErrorHandler(fire_dx11_onError);
                x->ShmAttach(w->d, &w->shm);
                x->Sync(w->d, 0);
                x->SetErrorHandler(old);
                shmctl(w->shm.shmid, IPC_RMID, NULL);   /* (gone as soon as both let go of it) */
                if (fire_dx11_lastError == 0) { w->imgW = width; w->imgH = height; return 1; }
                shmdt(w->shm.shmaddr);
            } else if (w->shm.shmid >= 0) shmctl(w->shm.shmid, IPC_RMID, NULL);
            w->image->data = NULL;
            if (w->image->f.destroy_image) w->image->f.destroy_image(w->image);
            w->image = NULL;
        }
        w->useShm = 0;
    }
    w->pixels = (uint32_t*)malloc((size_t)width * (size_t)height * 4);
    if (w->pixels == NULL) return 0;
    w->image = x->CreateImage(w->d, w->visual, (unsigned)w->depth, 2, 0, (char*)w->pixels, (unsigned)width, (unsigned)height, 32, 0);
    if (w->image == NULL) { free(w->pixels); w->pixels = NULL; return 0; }
    w->imgW = width; w->imgH = height;
    return 1;
}

/* Makes the X window (again, with a 32-bit visual when `argb`). */
static int fire_dx11_makeWindow(FireDx11Window* w, int argb) {
    FireXlib* x = w->x;
    FireXSetAttributes attr;
    unsigned long mask = 0x2 /* CWBackPixel */ | 0x8 /* CWBorderPixel */ | 0x800 /* CWEventMask */;
    FireXVisualInfo info;
    FireXId protocols[1];
    long pid;
    memset(&attr, 0, sizeof attr);
    w->visual = x->DefaultVisual(w->d, w->screen);
    w->depth = x->DefaultDepth(w->d, w->screen);
    w->argb = 0;
    if (argb && x->MatchVisualInfo(w->d, w->screen, 32, 4 /* TrueColor */, &info)) {
        w->visual = info.visual; w->depth = 32; w->argb = 1;
        w->colormap = x->CreateColormap(w->d, w->root, info.visual, 0 /* AllocNone */);
        attr.colormap = w->colormap;
        mask |= 0x2000 /* CWColormap */;
    }
    attr.event_mask = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x8000 | 0x20000 | 0x200000 | 0x400000;
    w->win = x->CreateWindow(w->d, w->root, 0, 0, (unsigned)w->winW, (unsigned)w->winH, 0, w->depth, 1 /* InputOutput */, w->visual, mask, &attr);
    if (w->win == 0) return 0;
    x->StoreName(w->d, w->win, w->title ? w->title : "");
    if (w->title) x->ChangeProperty(w->d, w->win, w->a.netWmName, w->a.utf8, 8, 0, (const unsigned char*)w->title, (int)strlen(w->title));
    {
        FireXClassHint hint;
        hint.res_name = (char*)"fire"; hint.res_class = (char*)"fire";
        x->SetClassHint(w->d, w->win, &hint);
    }
    protocols[0] = w->a.wmDelete;
    x->SetWMProtocols(w->d, w->win, protocols, 1);
    pid = (long)getpid();
    x->ChangeProperty(w->d, w->win, w->a.netWmPid, w->a.cardinal, 32, 0, (const unsigned char*)&pid, 1);
    { long version = 5; x->ChangeProperty(w->d, w->win, w->a.xdndAware, w->a.atom, 32, 0, (const unsigned char*)&version, 1); }
    w->gc = x->CreateGC(w->d, w->win, 0, NULL);
    /* fingers: XInput 2.2 touch events (the server then sends no emulated pointer events for them to this window) */
    if (x->XISelectEvents != NULL) {
        int event, error, major = 2, minor = 2;
        if (x->QueryExtension(w->d, "XInputExtension", &w->xiOpcode, &event, &error) && x->XIQueryVersion(w->d, &major, &minor) == 0 && (major > 2 || (major == 2 && minor >= 2))) {
            unsigned char bits[4] = { 0, 0, 0, 0 };
            FireXIEventMask m;
            bits[18 >> 3] |= (unsigned char)(1 << (18 & 7));   /* XI_TouchBegin */
            bits[19 >> 3] |= (unsigned char)(1 << (19 & 7));   /* XI_TouchUpdate */
            bits[20 >> 3] |= (unsigned char)(1 << (20 & 7));   /* XI_TouchEnd */
            m.deviceid = 1;   /* XIAllMasterDevices */
            m.mask_len = 4; m.mask = bits;
            w->touch = x->XISelectEvents(w->d, w->win, &m, 1) == 0;
        }
    }
    x->MapWindow(w->d, w->win);
    fire_dx11_openIM(w);
    if (w->cursor >= 0 && w->cursors[w->cursor]) x->DefineCursor(w->d, w->win, w->cursors[w->cursor]);
    x->Flush(w->d);
    return 1;
}

static void fire_dx11_destroyWindow(FireDx11Window* w) {
    FireXlib* x = w->x;
    fire_dx11_freeImage(w);
    if (w->ic) { x->DestroyIC(w->ic); w->ic = NULL; }
    if (w->im) { x->CloseIM(w->im); w->im = NULL; }
    if (w->gc) { x->FreeGC(w->d, w->gc); w->gc = NULL; }
    if (w->win) { x->DestroyWindow(w->d, w->win); w->win = 0; }
    if (w->colormap) { x->FreeColormap(w->d, w->colormap); w->colormap = 0; }
}

static int fire_dx11_open(FireDx11Window* w, const char* title, int width, int height, int vsync) {
    FireXlib* x = fire_dx11_lib();
    if (x == NULL || width <= 0 || height <= 0) return 0;
    w->x = x;
    w->d = x->OpenDisplay(NULL);
    if (w->d == NULL) return 0;
    w->vsync = vsync;
    w->winW = width; w->winH = height;
    w->title = (char*)fire_display_copyBytes(title ? title : "", strlen(title ? title : ""));
    w->screen = x->DefaultScreen(w->d);
    w->root = x->RootWindow(w->d, w->screen);
    fire_dx11_atoms(x, w->d, &w->a);
    w->scale = fire_dx11_readScale(x, w->d);
    fire_display_queueInit(&w->queue);
    if (!fire_dx11_makeWindow(w, 0)) { x->CloseDisplay(w->d); w->d = NULL; free(w->title); w->title = NULL; return 0; }
#ifdef FIRE_DISPLAY_HAS_SELFTEST
    fire_display_selftest(&w->queue, width, height);
#endif
    return 1;
}

static void fire_dx11_close(FireDx11Window* w) {
    FireXlib* x = w->x;
    int k;
    if (x == NULL || w->d == NULL) return;
    fire_dx11_destroyWindow(w);
    for (k = 0; k < FIRE_CURSOR_COUNT; k++) if (w->cursors[k]) { x->FreeCursor(w->d, w->cursors[k]); w->cursors[k] = 0; }
    x->CloseDisplay(w->d);
    w->d = NULL;
    free(w->title); w->title = NULL;
    free(w->preedit); w->preedit = NULL; w->preeditLen = w->preeditCap = 0;
    fire_display_queueFree(&w->queue);
}

static void fire_dx11_setVSync(FireDx11Window* w, int v) { w->vsync = v; }
static void fire_dx11_setTouchMouse(FireDx11Window* w, int v) { w->touchMouse = v; }

static void fire_dx11_setSize(FireDx11Window* w, int width, int height) {
    if (w->d == NULL) return;
    w->winW = width; w->winH = height;
    w->x->ResizeWindow(w->d, w->win, (unsigned)width, (unsigned)height);
    w->x->Flush(w->d);
}

static void fire_dx11_setCursor(FireDx11Window* w, int c) {
    /* the cursors of the theme (libXcursor), else those of the cursor font */
    static const char* const names[FIRE_CURSOR_COUNT] = { "default", "text", "wait", "crosshair", "progress", "nwse-resize", "nesw-resize", "ew-resize", "ns-resize", "move", "not-allowed", "pointer" };
    static const char* const oldNames[FIRE_CURSOR_COUNT] = { "left_ptr", "xterm", "watch", "crosshair", "left_ptr_watch", "bottom_right_corner", "bottom_left_corner", "sb_h_double_arrow", "sb_v_double_arrow", "fleur", "crossed_circle", "hand2" };
    static const unsigned font[FIRE_CURSOR_COUNT] = { 68, 152, 150, 34, 150, 14, 12, 108, 116, 52, 0, 60 };
    FireXlib* x = w->x;
    if (w->d == NULL || c == w->cursor) return;
    if (!w->cursors[c]) {
        if (x->CursorLibraryLoadCursor != NULL) {
            w->cursors[c] = x->CursorLibraryLoadCursor(w->d, names[c]);
            if (!w->cursors[c]) w->cursors[c] = x->CursorLibraryLoadCursor(w->d, oldNames[c]);
        }
        if (!w->cursors[c]) w->cursors[c] = x->CreateFontCursor(w->d, font[c]);
    }
    if (!w->cursors[c]) return;
    x->DefineCursor(w->d, w->win, w->cursors[c]);
    x->Flush(w->d);
    w->cursor = c;
}

static void fire_dx11_setTextInputRect(FireDx11Window* w, int x0, int y0, int width, int height) {
    w->textX = x0; w->textY = y0; w->textW = width; w->textH = height;
    if (w->ic != NULL && (w->imStyle & 0x4)) {
        FireXPoint spot;
        void* list;
        spot.x = (short)x0; spot.y = (short)(y0 + height);
        list = w->x->VaCreateNestedList(0, "spotLocation", &spot, (char*)NULL);
        if (list) { w->x->SetICValues(w->ic, "preeditAttributes", list, (char*)NULL); w->x->Free(list); }
    }
}

static int fire_dx11_setBackdrop(FireDx11Window* w, int kind) {
    FireXlib* x = w->x;
    int argb;
    if (w->d == NULL) return FIRE_BACKDROP_NONE;
    argb = kind > FIRE_BACKDROP_NONE && fire_dx11_compositor(w);
    if (argb != w->argb) {
        /* the visual of an X window cannot change: it is made anew */
        fire_dx11_destroyWindow(w);
        if (!fire_dx11_makeWindow(w, argb)) { w->closed = 1; return FIRE_BACKDROP_NONE; }
    }
    if (!w->argb) { w->backdrop = FIRE_BACKDROP_NONE; return w->backdrop; }
    w->backdrop = FIRE_BACKDROP_TRANSPARENT;
    if (kind >= FIRE_BACKDROP_BLUR) {
        /* KWin blurs behind a window that asks (an empty region: the whole window) */
        FireXId type = 0;
        size_t n = 0, i;
        unsigned char* supported = fire_dx11_readProperty(x, w->d, w->root, w->a.netSupported, 0, &type, &n);
        int kwin = 0;
        for (i = 0; supported != NULL && i + sizeof(long) <= n; i += sizeof(long)) if ((FireXId)((long*)(void*)(supported + i))[0] == w->a.kdeBlur) kwin = 1;
        free(supported);
        if (kwin) { x->ChangeProperty(w->d, w->win, w->a.kdeBlur, w->a.cardinal, 32, 0, (const unsigned char*)"", 0); w->backdrop = FIRE_BACKDROP_BLUR; }
    } else x->DeleteProperty(w->d, w->win, w->a.kdeBlur);
    x->Flush(w->d);
    return w->backdrop;
}

static double fire_dx11_scale(FireDx11Window* w) { return w->scale; }

/* ---- events ---- */

/* The modifiers that are down: the keys this window saw, checked against the state the server reports (a key pressed while another window had the keyboard). */
static int fire_dx11_mods(FireDx11Window* w, unsigned state) {
    int m = w->mods;
    if (!(state & 1)) m &= ~(FIRE_MOD_LSHIFT | FIRE_MOD_RSHIFT); else if (!(m & (FIRE_MOD_LSHIFT | FIRE_MOD_RSHIFT))) m |= FIRE_MOD_LSHIFT;
    if (!(state & 4)) m &= ~(FIRE_MOD_LCTRL | FIRE_MOD_RCTRL); else if (!(m & (FIRE_MOD_LCTRL | FIRE_MOD_RCTRL))) m |= FIRE_MOD_LCTRL;
    if (!(state & 8)) m &= ~(FIRE_MOD_LALT | FIRE_MOD_RALT); else if (!(m & (FIRE_MOD_LALT | FIRE_MOD_RALT))) m |= FIRE_MOD_LALT;
    if (!(state & 0x40)) m &= ~(FIRE_MOD_LGUI | FIRE_MOD_RGUI); else if (!(m & (FIRE_MOD_LGUI | FIRE_MOD_RGUI))) m |= FIRE_MOD_LGUI;
    m &= ~(FIRE_MOD_CAPS | FIRE_MOD_NUM | FIRE_MOD_MODE);
    if (state & 2) m |= FIRE_MOD_CAPS;
    if (state & 0x10) m |= FIRE_MOD_NUM;
    if (state & 0x80) m |= FIRE_MOD_MODE;
    w->mods = m & ~(FIRE_MOD_CAPS | FIRE_MOD_NUM | FIRE_MOD_MODE);
    return m;
}

/* The character of a keysym (0: none) - Latin-1 directly, Unicode keysyms, and the few others the main block of a keyboard has. */
static uint32_t fire_dx11_keysymChar(unsigned long sym) {
    if ((sym >= 0x20 && sym <= 0x7E) || (sym >= 0xA0 && sym <= 0xFF)) return (uint32_t)sym;
    if ((sym & 0xFF000000ul) == 0x01000000ul) return (uint32_t)(sym & 0x00FFFFFFul);
    switch (sym) {
        case 0xFE50: return '`';   /* dead_grave */
        case 0xFE51: return 0xB4;  /* dead_acute */
        case 0xFE52: return '^';   /* dead_circumflex */
        case 0xFE53: return '~';   /* dead_tilde */
        case 0xFE57: return 0xA8;  /* dead_diaeresis */
        default: return 0;
    }
}

static void fire_dx11_key(FireDx11Window* w, FireXEv* e) {
    FireXlib* x = w->x;
    int down = e->type == FIRE_X_KEY_PRESS;
    int sc = fire_key_fromEvdev(e->xkey.keycode >= 8 ? e->xkey.keycode - 8 : 0);
    int bit = fire_key_modBit(sc), mods, repeat = 0;
    unsigned long sym;
    if (!down && x->Pending(w->d) > 0) {
        /* (a release followed at once by a press of the same key at the same time: the auto-repeat of the server - the release is left out, the press is a repeat) */
        FireXEv next;
        x->PeekEvent(w->d, &next);
        if (next.type == FIRE_X_KEY_PRESS && next.xkey.keycode == e->xkey.keycode && next.xkey.time == e->xkey.time) { w->repeatNext = 1; return; }
    }
    if (down) { repeat = w->repeatNext; w->repeatNext = 0; }
    if (bit) { if (down) w->mods |= bit; else w->mods &= ~bit; }
    mods = fire_dx11_mods(w, e->xkey.state);
    if (bit) { if (down) mods |= bit; else mods &= ~bit; }
    if (sc == FIRE_SC_CAPSLOCK && down) mods ^= FIRE_MOD_CAPS;
    if (sc == FIRE_SC_NUMLOCK && down) mods ^= FIRE_MOD_NUM;
    sym = x->LookupKeysym(&e->xkey, 0);
    fire_display_queueKey(&w->queue, down, fire_key_code(sc, fire_dx11_keysymChar(sym)), sc, mods, repeat);
    /* the text the key types (through the input method; without one the layout's character) */
    if (down) {
        char buf[256];
        unsigned long keysym = 0;
        int status = 0, n = 0;
        if (w->ic != NULL) n = x->Utf8LookupString(w->ic, &e->xkey, buf, (int)sizeof buf - 1, &keysym, &status);
        else {
            uint32_t c = fire_dx11_keysymChar(x->LookupKeysym(&e->xkey, (e->xkey.state & 1) ? 1 : 0));
            if (c != 0) n = fire_display_utf8(c, buf);
            status = 2;
        }
        if (n > 0 && (status == 2 || status == 4)) fire_display_queueText(&w->queue, buf, (size_t)n);
    }
}

static void fire_dx11_dndStatus(FireDx11Window* w, int accept) {
    FireXEv r;
    memset(&r, 0, sizeof r);
    r.xclient.type = FIRE_X_CLIENT_MESSAGE;
    r.xclient.display = w->d;
    r.xclient.window = w->dndSource;
    r.xclient.message_type = w->a.xdndStatus;
    r.xclient.format = 32;
    r.xclient.data.l[0] = (long)w->win;
    r.xclient.data.l[1] = accept ? 3 : 2;   /* accepted; send positions while over us */
    r.xclient.data.l[4] = accept ? (long)w->a.xdndActionCopy : 0;
    w->x->SendEvent(w->d, w->dndSource, 0, 0, &r);
}
static void fire_dx11_dndFinished(FireDx11Window* w, int accepted) {
    FireXEv r;
    memset(&r, 0, sizeof r);
    r.xclient.type = FIRE_X_CLIENT_MESSAGE;
    r.xclient.display = w->d;
    r.xclient.window = w->dndSource;
    r.xclient.message_type = w->a.xdndFinished;
    r.xclient.format = 32;
    r.xclient.data.l[0] = (long)w->win;
    r.xclient.data.l[1] = accepted ? 1 : 0;
    r.xclient.data.l[2] = accepted ? (long)w->a.xdndActionCopy : 0;
    w->x->SendEvent(w->d, w->dndSource, 0, 0, &r);
}
/* The best of the types a drag offers: paths, else text (0: nothing for us). */
static FireXId fire_dx11_dndPick(FireDx11Window* w, const FireXId* types, size_t n) {
    size_t i;
    for (i = 0; i < n; i++) if (types[i] == w->a.uriList) return types[i];
    for (i = 0; i < n; i++) if (types[i] == w->a.utf8 || types[i] == w->a.textPlainUtf8) return types[i];
    for (i = 0; i < n; i++) if (types[i] == w->a.textPlain || types[i] == w->a.string) return types[i];
    return 0;
}

static void fire_dx11_clientMessage(FireDx11Window* w, FireXEv* e) {
    FireXlib* x = w->x;
    FireXClient* m = &e->xclient;
    if (m->message_type == w->a.wmProtocols && (FireXId)m->data.l[0] == w->a.wmDelete) { fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE_REQUEST); return; }
    if (m->message_type == w->a.xdndEnter) {
        w->dndSource = (FireXId)m->data.l[0];
        w->dndVersion = (int)((unsigned long)m->data.l[1] >> 24);
        if (m->data.l[1] & 1) {
            size_t n = 0;
            FireXId type = 0;
            unsigned char* list = fire_dx11_readProperty(x, w->d, w->dndSource, w->a.xdndTypeList, 0, &type, &n);
            w->dndType = list != NULL ? fire_dx11_dndPick(w, (const FireXId*)(void*)list, n / sizeof(long)) : 0;
            free(list);
        } else {
            FireXId types[3];
            types[0] = (FireXId)m->data.l[2]; types[1] = (FireXId)m->data.l[3]; types[2] = (FireXId)m->data.l[4];
            w->dndType = fire_dx11_dndPick(w, types, 3);
        }
        return;
    }
    if (m->message_type == w->a.xdndPosition) {
        int wx = 0, wy = 0;
        FireXId child;
        w->dndSource = (FireXId)m->data.l[0];
        x->TranslateCoordinates(w->d, w->root, w->win, (int)((unsigned long)m->data.l[2] >> 16), (int)(m->data.l[2] & 0xFFFF), &wx, &wy, &child);
        w->dndX = (float)wx; w->dndY = (float)wy;
        if (w->dndType != 0) fire_display_dropHover(&w->queue, w->dndX, w->dndY);
        fire_dx11_dndStatus(w, w->dndType != 0);
        x->Flush(w->d);
        return;
    }
    if (m->message_type == w->a.xdndLeave) { fire_display_dropLeave(&w->queue); w->dndType = 0; return; }
    if (m->message_type == w->a.xdndDrop) {
        w->dndSource = (FireXId)m->data.l[0];
        if (w->dndType == 0) { fire_dx11_dndFinished(w, 0); fire_display_dropLeave(&w->queue); x->Flush(w->d); return; }
        x->ConvertSelection(w->d, w->a.xdndSelection, w->dndType, w->a.xdndSelection, w->win, (FireXId)m->data.l[2]);
        x->Flush(w->d);
    }
}

/* The answer to the conversion of a drop: the paths or the text. */
static void fire_dx11_dropData(FireDx11Window* w, FireXEv* e) {
    FireXlib* x = w->x;
    size_t n = 0;
    FireXId type = 0;
    unsigned char* data = e->xselection.property != 0 ? fire_dx11_readProperty(x, w->d, w->win, e->xselection.property, 1, &type, &n) : NULL;
    if (data != NULL) {
        if (w->dndType == w->a.uriList) {
            char* paths = fire_display_pathsFromUriList((const char*)data, n);
            if (paths != NULL) { fire_display_dropFile(&w->queue, paths, strlen(paths)); free(paths); }
        } else fire_display_dropText(&w->queue, (const char*)data, n);
        free(data);
    }
    fire_display_dropDone(&w->queue, w->dndX, w->dndY);
    fire_dx11_dndFinished(w, data != NULL);
    w->dndType = 0;
    x->Flush(w->d);
}

/* A finger (XInput 2.2). */
static void fire_dx11_touch(FireDx11Window* w, FireXIDeviceEvent* t) {
    int type = t->evtype == 18 ? FIRE_EV_TOUCH_DOWN : t->evtype == 19 ? FIRE_EV_TOUCH_MOVE : FIRE_EV_TOUCH_UP;
    float fx = (float)t->event_x, fy = (float)t->event_y;
    fire_display_queueTouch(&w->queue, type, (long long)t->detail, fx, fy, type == FIRE_EV_TOUCH_UP ? 0.0f : 1.0f);
    /* the first finger also moves the mouse (TouchMouse) */
    if (!w->touchMouse) return;
    if (type == FIRE_EV_TOUCH_DOWN && w->touchMouseFinger < 0) {
        w->touchMouseFinger = t->detail;
        fire_display_queueMotion(&w->queue, fx, fy);
        fire_display_queueButton(&w->queue, 1, FIRE_BUTTON_LEFT, fx, fy);
    } else if (t->detail == w->touchMouseFinger) {
        fire_display_queueMotion(&w->queue, fx, fy);
        if (type == FIRE_EV_TOUCH_UP) { fire_display_queueButton(&w->queue, 0, FIRE_BUTTON_LEFT, fx, fy); w->touchMouseFinger = -1; }
    }
}

static int fire_dx11_pump(FireDx11Window* w, FireDisplayEvents* out, int fbWidth, int fbHeight) {
    FireXlib* x = w->x;
    if (w->d == NULL) return 0;
    while (!w->closed && x->Pending(w->d) > 0) {
        FireXEv e;
        x->NextEvent(w->d, &e);
        /* (what the input method takes - a key of a composition - it gives back as text or as a key of its own later) */
        if (x->FilterEvent(&e, 0)) continue;
        switch (e.type) {
            case FIRE_X_KEY_PRESS: case FIRE_X_KEY_RELEASE: fire_dx11_key(w, &e); break;
            case FIRE_X_BUTTON_PRESS: case FIRE_X_BUTTON_RELEASE: {
                unsigned b = e.xbutton.button;
                int down = e.type == FIRE_X_BUTTON_PRESS;
                float fx = (float)e.xbutton.x, fy = (float)e.xbutton.y;
                if (b >= 4 && b <= 7) {
                    if (down) fire_display_queueScroll(&w->queue, b == 6 ? -1.0f : b == 7 ? 1.0f : 0.0f, b == 4 ? 1.0f : b == 5 ? -1.0f : 0.0f, fx, fy);
                } else {
                    int button = b == 1 ? FIRE_BUTTON_LEFT : b == 2 ? FIRE_BUTTON_MIDDLE : b == 3 ? FIRE_BUTTON_RIGHT : b == 8 ? FIRE_BUTTON_X1 : b == 9 ? FIRE_BUTTON_X2 : 0;
                    if (button) fire_display_queueButton(&w->queue, down, button, fx, fy);
                }
                break;
            }
            case FIRE_X_MOTION: fire_display_queueMotion(&w->queue, (float)e.xmotion.x, (float)e.xmotion.y); break;
            case FIRE_X_ENTER: fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_ENTER); break;
            case FIRE_X_LEAVE: fire_display_queueSimple(&w->queue, FIRE_EV_MOUSE_LEAVE); break;
            case FIRE_X_FOCUS_IN:
                if (e.xfocus.mode == 0 /* NotifyNormal */ || e.xfocus.mode == 3 /* NotifyWhileGrabbed */) {
                    if (w->ic) x->SetICFocus(w->ic);
                    fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_GAINED);
                }
                break;
            case FIRE_X_FOCUS_OUT:
                if (e.xfocus.mode == 0 || e.xfocus.mode == 3) {
                    if (w->ic) x->UnsetICFocus(w->ic);
                    w->mods = 0;
                    fire_display_queueSimple(&w->queue, FIRE_EV_FOCUS_LOST);
                }
                break;
            case FIRE_X_EXPOSE:
                if (e.xexpose.count == 0 && w->image != NULL && !w->direct) {
                    if (w->useShm) x->ShmPutImage(w->d, w->win, w->gc, w->image, 0, 0, 0, 0, (unsigned)w->imgW, (unsigned)w->imgH, 0);
                    else x->PutImage(w->d, w->win, w->gc, w->image, 0, 0, 0, 0, (unsigned)w->imgW, (unsigned)w->imgH);
                }
                break;
            case FIRE_X_CONFIGURE:
                if (e.xconfigure.window == w->win && (e.xconfigure.width != w->winW || e.xconfigure.height != w->winH)) {
                    w->winW = e.xconfigure.width; w->winH = e.xconfigure.height;
                    fire_display_queueResize(&w->queue, w->winW, w->winH);
                }
                break;
            case FIRE_X_CLIENT_MESSAGE: fire_dx11_clientMessage(w, &e); break;
            case FIRE_X_SELECTION_NOTIFY: if (e.xselection.selection == w->a.xdndSelection) fire_dx11_dropData(w, &e); break;
            case FIRE_X_MAPPING: x->RefreshKeyboardMapping(&e); break;
            case FIRE_X_GENERIC:
                if (e.xcookie.extension == w->xiOpcode && x->GetEventData(w->d, &e.xcookie)) {
                    if (e.xcookie.evtype >= 18 && e.xcookie.evtype <= 20) fire_dx11_touch(w, (FireXIDeviceEvent*)e.xcookie.data);
                    x->FreeEventData(w->d, &e.xcookie);
                }
                break;
            default: break;
        }
    }
    if (w->closed) fire_display_queueSimple(&w->queue, FIRE_EV_CLOSE);
    fire_display_queueTake(&w->queue, out, w->winW, w->winH, fbWidth, fbHeight);
    return !w->queue.quit;
}

/* Shows the pixels (R,G,B,A per 32-bit word), stretched to the window: converted to the pixels of X (B,G,R,X in memory; premultiplied with a 32-bit visual). */
static int fire_gfx_dx11_clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

/* Shows the frame; only the area (ax, ay, aw, ah) changed since the last one - when the frame is not stretched and the image is the same, only that is converted and
 * handed to the X server (the rest of the image still holds the last frame; an Expose puts the whole image). */
static void fire_dx11_presentArea(FireDx11Window* w, const uint32_t* rgba, int width, int height, int ax, int ay, int aw, int ah) {
    FireXlib* x = w->x;
    int ww = w->winW, wh = w->winH, row, c0 = 0, c1, r0 = 0, r1;
    uint32_t* dst;
    size_t stride;
    if (w->d == NULL || w->direct || rgba == NULL || width <= 0 || height <= 0 || ww <= 0 || wh <= 0) return;
    if (w->imgW != ww || w->imgH != wh) {
        if (!fire_dx11_makeImage(w, ww, wh)) return;
        ax = 0; ay = 0; aw = width; ah = height;   /* (a new image: all of it) */
    }
    c1 = ww; r1 = wh;
    if (ww == width && wh == height) {
        c0 = fire_gfx_dx11_clamp(ax, 0, ww); c1 = fire_gfx_dx11_clamp(ax + aw, 0, ww);
        r0 = fire_gfx_dx11_clamp(ay, 0, wh); r1 = fire_gfx_dx11_clamp(ay + ah, 0, wh);
        if (c1 <= c0 || r1 <= r0) { if (w->vsync) fire_dx11_pace(&w->lastFrame); return; }
    }
    dst = (uint32_t*)(void*)w->image->data;
    stride = (size_t)w->image->bytes_per_line / 4;
    for (row = r0; row < r1; row++) {
        const uint32_t* src = rgba + (size_t)(ww == width && wh == height ? row : (int)((long long)row * height / wh)) * (size_t)width;
        uint32_t* d = dst + (size_t)row * stride;
        int col;
        if (ww == width) {
            if (!w->argb) for (col = c0; col < c1; col++) { uint32_t v = src[col]; d[col] = 0xFF000000u | ((v & 0xFFu) << 16) | (v & 0xFF00u) | ((v >> 16) & 0xFFu); }
            else for (col = c0; col < c1; col++) {
                uint32_t v = src[col], a = v >> 24;
                uint32_t r = (v & 0xFFu) * a / 255u, g = ((v >> 8) & 0xFFu) * a / 255u, b = ((v >> 16) & 0xFFu) * a / 255u;
                d[col] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        } else {
            /* (stretched: the nearest pixel, stepping in 16.16) */
            uint32_t step = (uint32_t)(((uint64_t)width << 16) / (uint32_t)ww), pos = step / 2;
            for (col = 0; col < ww; col++, pos += step) {
                uint32_t v = src[pos >> 16], a = w->argb ? v >> 24 : 255u;
                uint32_t r = (v & 0xFFu) * a / 255u, g = ((v >> 8) & 0xFFu) * a / 255u, b = ((v >> 16) & 0xFFu) * a / 255u;
                d[col] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }
    }
    if (w->vsync) fire_dx11_pace(&w->lastFrame);
    if (w->useShm) { x->ShmPutImage(w->d, w->win, w->gc, w->image, c0, r0, c0, r0, (unsigned)(c1 - c0), (unsigned)(r1 - r0), 0); x->Sync(w->d, 0); }
    else { x->PutImage(w->d, w->win, w->gc, w->image, c0, r0, c0, r0, (unsigned)(c1 - c0), (unsigned)(r1 - r0)); x->Flush(w->d); }
}

static void fire_dx11_present(FireDx11Window* w, const uint32_t* rgba, int width, int height) { fire_dx11_presentArea(w, rgba, width, height, 0, 0, width, height); }


static int fire_dx11_native(FireDx11Window* w, fire_native_window* out) {
    if (w->d == NULL) return 0;
    out->kind = FIRE_NATIVE_X11;
    out->a = w->d;
    out->id = (uint64_t)w->win;
    out->width = w->winW; out->height = w->winH;
    fire_dx11_freeImage(w);
    w->direct = 1;
    return 1;
}
static void fire_dx11_restore(FireDx11Window* w) { w->direct = 0; }

/* Dragging text and files out of the window (fire_dragout.h: XDND; it reads the events it needs on the window's connection itself). */
static int fire_dx11_startDrag(FireDx11Window* w, const char* text, const char* files) {
    if (w->d == NULL) return 0;
    return fire_dragout_x11(w->d, (unsigned long)w->win, text, files);
}
static int fire_dx11_canStartDrag(FireDx11Window* w) { return w->d != NULL && fire_x11_load() != NULL; }

#endif
