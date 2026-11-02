/* fire native platform layer, display part in C (the HAL of the windows bridge): the events a display delivers, a list of them, and what the desktop says about how
 * programs should look (the system theme).
 *
 * A platform package offers `fire_display.h` (FIRE_PLATFORM_DISPLAY_C_HEADER points to it) with:
 *   FIRE_DISPLAY_AVAILABLE                                    1: a window can be shown; 0: `new Window(...)` throws a HandleUnavailableException
 *   FireDisplayWindow                                         one window (a struct of the implementation)
 *   void fire_display_init(FireDisplayWindow*)                an unopened window (VSync and TouchMouse on)
 *   int  fire_display_open(w, title (UTF-8), width, height, vsync)    1: it is open
 *   void fire_display_setVSync / setSize(w, width, height) / setCursor(w, SDL system cursor) / setTouchMouse; int fire_display_vsync / touchMouse
 *   int  fire_display_pump(w, FireDisplayEvents* out, fbWidth, fbHeight)    takes the pending events (positions in framebuffer pixels); 0 once the window was closed
 *   void fire_display_present(w, const uint32_t* rgba, width, height)       shows the pixels (R,G,B,A per 32-bit word), stretched to the window
 *   void fire_display_close(w)                                closes it (again: nothing)
 *   void fire_display_threadDone(void)                        a thread of the program that used the display ends
 *   void fire_display_systemTheme(FireSystemTheme*)           the look of the desktop
 *   int  fire_display_clipboardSet(text (UTF-8))              puts text on the clipboard of the desktop (1: done)
 *   char* fire_display_clipboardGet(void)                     the text on the clipboard (malloc, UTF-8; NULL: none)
 *   int  fire_display_clipboardHas(void)                      is there text on the clipboard?
 *   unsigned char* fire_display_clipboardGetImage(size_t* size)   the picture on the clipboard: the bytes of a PNG or BMP file (malloc; NULL: none)
 *   int  fire_display_clipboardSetImage(png, pngSize, bmp, bmpSize)  puts a picture there - as image/png and, where the desktop wants it, image/bmp (bmp may be NULL)
 *   int  fire_display_clipboardHasImage(void)
 *   char* fire_display_clipboardGetFiles(void)                the paths on the clipboard, one per line (malloc, UTF-8; NULL: none)
 *   int  fire_display_clipboardSetFiles(paths)                puts paths there (one per line; text/uri-list on the desktop)
 *   int  fire_display_clipboardHasFiles(void)
 *   int  fire_display_startDrag(w, text, paths)                 drags the text and/or the paths (one per line; both UTF-8, "" for none) out of the window into other programs
 *                                                             until the user lets go: 0 nothing, 1 copied, 2 moved (Window.StartDrag; fire_dragout.h does it for Windows and
 *                                                             X11). A display that has it defines FIRE_DISPLAY_START_DRAG; without it the bridge says 0.
 * (A display without such a clipboard returns NULL / 0; one without a desktop keeps all kinds itself: fire_display_memClipboard below.)
 * A desktop also reports what the user drops onto a window: FIRE_EV_DROP_FILES (`data`: the paths, one per line) and FIRE_EV_DROP_TEXT (`data`: the text), at
 * the position of the drop; while something of another program is dragged over the window, FIRE_EV_DRAG_HOVER (x, y), and FIRE_EV_DRAG_LEAVE when it leaves
 * without being dropped (SDL3; SDL2 sends neither).
 * A board package of your own provides its own `fire_display.h` (see fire_display_sdl.h for what the functions do) and puts the pixels on its screen. */
#ifndef FIRE_DISPLAY_C_H
#define FIRE_DISPLAY_C_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/* The numbers are those of `EventType` in the windows prelude. */
enum { FIRE_EV_UNKNOWN = 0, FIRE_EV_CLOSE = 1, FIRE_EV_CLOSE_REQUEST = 2, FIRE_EV_TEXT_INPUT = 3, FIRE_EV_RESIZE = 4, FIRE_EV_TOUCH_DOWN = 16, FIRE_EV_TOUCH_MOVE = 17, FIRE_EV_TOUCH_UP = 18,
       FIRE_EV_MOUSE_DOWN = 8, FIRE_EV_MOUSE_MOVE = 9, FIRE_EV_MOUSE_MOVE_REL = 10, FIRE_EV_MOUSE_UP = 11, FIRE_EV_MOUSE_SCROLL = 12, FIRE_EV_KEY_DOWN = 24, FIRE_EV_KEY_UP = 25,
       FIRE_EV_JOY_AXIS = 32, FIRE_EV_JOY_BUTTON_DOWN = 33, FIRE_EV_JOY_BUTTON_UP = 34, FIRE_EV_JOY_HAT = 35, FIRE_EV_JOY_ADDED = 36, FIRE_EV_JOY_REMOVED = 37,
       FIRE_EV_DROP_FILES = 40, FIRE_EV_DROP_TEXT = 41, FIRE_EV_DRAG_HOVER = 42, FIRE_EV_DRAG_LEAVE = 43 };

/* One event; which fields count depends on `type` (positions are in framebuffer pixels: the window may be scaled). */
typedef struct FireDisplayEvent {
    int type;
    int keyCode, scanCode, modifier;   /* keys */
    int repeat;
    int button;                        /* mouse buttons */
    int buttonState;                   /* mouse motion: the buttons that are down */
    float x, y, xrel, yrel;            /* mouse position / movement */
    float scrollX, scrollY;
    char text[32];                     /* text input (UTF-8, terminated) */
    int width, height;                 /* resize: the new size of the window */
    long long finger;                  /* touch: which finger; x, y are in framebuffer pixels */
    float pressure;                    /* touch: 0 to 1 */
    int joystick, index;               /* joystick: the device, and the axis / button / hat */
    float value;                       /* joystick axis: -1 to 1; hat: a bit mask (1 up, 2 right, 4 down, 8 left) */
    char* data;                        /* a drop: the paths (one per line) or the text (UTF-8, malloc; it belongs to the list that holds the event) */
} FireDisplayEvent;

static void fire_display_eventInit(FireDisplayEvent* e) { memset(e, 0, sizeof *e); e->type = FIRE_EV_UNKNOWN; }

/* A list of events. */
typedef struct FireDisplayEvents { FireDisplayEvent* items; size_t count, cap; } FireDisplayEvents;
/* Appends a copy - with a copy of its `data` (0 without memory: the event is lost). */
static int fire_display_push(FireDisplayEvents* l, const FireDisplayEvent* e) {
    FireDisplayEvent copy = *e;
    if (e->data != NULL) {
        size_t n = strlen(e->data);
        copy.data = (char*)malloc(n + 1);
        if (copy.data == NULL) return 0;
        memcpy(copy.data, e->data, n + 1);
    }
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 16;
        FireDisplayEvent* grown = (FireDisplayEvent*)realloc(l->items, sizeof(FireDisplayEvent) * cap);
        if (grown == NULL) { free(copy.data); return 0; }
        l->items = grown;
        l->cap = cap;
    }
    l->items[l->count++] = copy;
    return 1;
}
/* Empties the list (the `data` of its events is freed; the memory of the list stays for the next events). */
static void fire_display_eventsClear(FireDisplayEvents* l) {
    size_t i;
    for (i = 0; i < l->count; i++) { free(l->items[i].data); l->items[i].data = NULL; }
    l->count = 0;
}
static void fire_display_eventsFree(FireDisplayEvents* l) { fire_display_eventsClear(l); free(l->items); l->items = NULL; l->count = l->cap = 0; }

/* What the desktop says about how programs should look (`SystemTheme.Current()` of `#import "windows"`). */
typedef struct FireSystemTheme {
    int embedded;      /* a board without a desktop */
    double scale;      /* logical pixels -> pixels of the screen */
    int dark;          /* 1: dark mode */
    int accent;        /* 0xRRGGBB */
    char fonts[512];   /* candidates for the font of the interface, the best first, separated by ';' ("" = none known) */
    int fontSize;      /* the height of the letters in logical pixels (0 = not known) */
} FireSystemTheme;

/* The defaults of a desktop (scale 1, light, the accent 0x0078D7, no font). */
static void fire_display_themeDefaults(FireSystemTheme* t) {
    t->embedded = 0;
    t->scale = 1.0;
    t->dark = 0;
    t->accent = 0x0078D7;
    t->fonts[0] = 0;
    t->fontSize = 0;
}

/* ---- the clipboard beyond text ---- */

/* A copy of `n` bytes (malloc, with a terminating 0 after them; NULL without memory). */
static unsigned char* fire_display_copyBytes(const void* data, size_t n) {
    unsigned char* copy = (unsigned char*)malloc(n + 1);
    if (copy == NULL) return NULL;
    if (n) memcpy(copy, data, n);
    copy[n] = 0;
    return copy;
}

/* The paths (one per line) as a text/uri-list: `file://` URIs, one per line ending in CR LF, with the bytes that a URI may not hold as %XX
 * (malloc; NULL without memory). A Windows path (C:\a\b) becomes file:///C:/a/b. */
static char* fire_display_uriListFromPaths(const char* paths) {
    static const char hex[] = "0123456789ABCDEF";
    size_t n = strlen(paths), o = 0;
    char* out = (char*)malloc(n * 3 + (n + 1) * 10 + 1);
    const char* p = paths;
    if (out == NULL) return NULL;
    while (*p) {
        const char* end = strchr(p, '\n');
        size_t len = end ? (size_t)(end - p) : strlen(p), i;
        if (len > 0) {
            memcpy(out + o, "file://", 7); o += 7;
            if (len >= 2 && p[1] == ':') out[o++] = '/';   /* (a drive letter) */
            for (i = 0; i < len; i++) {
                unsigned char c = (unsigned char)p[i];
                if (c == '\\') c = '/';
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || strchr("/-_.~:!$&'()*+,;=@", c) != NULL) out[o++] = (char)c;
                else { out[o++] = '%'; out[o++] = hex[c >> 4]; out[o++] = hex[c & 15]; }
            }
            out[o++] = '\r'; out[o++] = '\n';
        }
        p = end ? end + 1 : p + len;
    }
    out[o] = 0;
    return out;
}

static int fire_display_hexValue(char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; }

/* The paths of a text/uri-list (`n` bytes): the file:// URIs as paths, one per line; comments (#) and other URIs are left out (malloc; NULL: no path). On
 * Windows the slashes become backslashes (file:///C:/a/b -> C:\a\b). */
static char* fire_display_pathsFromUriList(const char* list, size_t n) {
    char* out = (char*)malloc(n + 1);
    size_t i = 0, o = 0;
    if (out == NULL) return NULL;
    while (i < n) {
        size_t start = i, end;
        while (i < n && list[i] != '\n' && list[i] != '\r' && list[i] != 0) i++;
        end = i;
        while (i < n && (list[i] == '\n' || list[i] == '\r' || list[i] == 0)) i++;
        if (end - start > 7 && strncmp(list + start, "file://", 7) == 0) {
            size_t k = start + 7;
            if (o > 0) out[o++] = '\n';
            /* file://host/path: the host (usually empty, or localhost) is skipped */
            while (k < end && list[k] != '/') k++;
#ifdef _WIN32
            if (end - k >= 3 && list[k + 2] == ':') k++;   /* /C:/... -> C:/... */
#endif
            while (k < end) {
                char c = list[k];
                if (c == '%' && k + 2 < end && fire_display_hexValue(list[k + 1]) >= 0 && fire_display_hexValue(list[k + 2]) >= 0) {
                    c = (char)(fire_display_hexValue(list[k + 1]) * 16 + fire_display_hexValue(list[k + 2]));
                    k += 3;
                } else k++;
#ifdef _WIN32
                if (c == '/') c = '\\';
#endif
                out[o++] = c;
            }
        }
    }
    out[o] = 0;
    if (o == 0) { free(out); return NULL; }
    return out;
}

/* A clipboard of the program's own (a display without a desktop, and a desktop that cannot be reached): one kind at a time, as on a desktop -
 * putting text there takes the picture away, and so on. */
typedef struct FireDisplayMemClipboard {
    char* text;
    unsigned char* image; size_t imageSize;
    char* files;
} FireDisplayMemClipboard;

static FireDisplayMemClipboard* fire_display_memClipboard(void) { static FireDisplayMemClipboard clip; return &clip; }
static void fire_display_memClear(FireDisplayMemClipboard* c) {
    free(c->text); free(c->image); free(c->files);
    c->text = NULL; c->image = NULL; c->files = NULL; c->imageSize = 0;
}
static int fire_display_memSetText(const char* text) {
    FireDisplayMemClipboard* c = fire_display_memClipboard();
    char* copy = (char*)fire_display_copyBytes(text, strlen(text));
    if (copy == NULL) return 0;
    fire_display_memClear(c);
    c->text = copy;
    return 1;
}
static char* fire_display_memGetText(void) {
    FireDisplayMemClipboard* c = fire_display_memClipboard();
    if (c->text == NULL || c->text[0] == 0) return NULL;
    return (char*)fire_display_copyBytes(c->text, strlen(c->text));
}
static int fire_display_memHasText(void) { FireDisplayMemClipboard* c = fire_display_memClipboard(); return c->text != NULL && c->text[0] != 0; }
static int fire_display_memSetImage(const unsigned char* png, size_t size) {
    FireDisplayMemClipboard* c = fire_display_memClipboard();
    unsigned char* copy;
    if (png == NULL || size == 0) return 0;
    copy = fire_display_copyBytes(png, size);
    if (copy == NULL) return 0;
    fire_display_memClear(c);
    c->image = copy;
    c->imageSize = size;
    return 1;
}
static unsigned char* fire_display_memGetImage(size_t* size) {
    FireDisplayMemClipboard* c = fire_display_memClipboard();
    *size = 0;
    if (c->image == NULL) return NULL;
    *size = c->imageSize;
    return fire_display_copyBytes(c->image, c->imageSize);
}
static int fire_display_memHasImage(void) { return fire_display_memClipboard()->image != NULL; }
static int fire_display_memSetFiles(const char* paths) {
    FireDisplayMemClipboard* c = fire_display_memClipboard();
    char* copy;
    if (paths == NULL || paths[0] == 0) return 0;
    copy = (char*)fire_display_copyBytes(paths, strlen(paths));
    if (copy == NULL) return 0;
    fire_display_memClear(c);
    c->files = copy;
    return 1;
}
static char* fire_display_memGetFiles(void) {
    FireDisplayMemClipboard* c = fire_display_memClipboard();
    if (c->files == NULL) return NULL;
    return (char*)fire_display_copyBytes(c->files, strlen(c->files));
}
static int fire_display_memHasFiles(void) { return fire_display_memClipboard()->files != NULL; }

#endif
