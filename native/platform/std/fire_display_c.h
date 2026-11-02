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
 * A desktop also reports what the user drops onto a window: FIRE_EV_DROP_FILES (`data`: the paths, one per line) and FIRE_EV_DROP_TEXT (`data`: the text), at
 * the position of the drop.
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
       FIRE_EV_DROP_FILES = 40, FIRE_EV_DROP_TEXT = 41 };

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

#endif
