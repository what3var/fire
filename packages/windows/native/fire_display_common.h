/* fire native platform package "std": what fire's own displays share (fire_display_win32.h, _x11.h, _wayland.h, _cocoa.h, _web.h, _headless.h) - the queue of a window,
 * the conversion of positions to framebuffer pixels, text in pieces, compositions of an input method, drops, and the events the tests push (FIRE_DISPLAY_SELFTEST*).
 *
 * A display collects the events of a window in its queue with positions in pixels of the window; fire_display_queueTake hands them to the bridge in framebuffer pixels
 * (the framebuffer is stretched over the window, its size is only known when the bridge pumps). Nothing here needs a lock: a window and its queue belong to the thread
 * that opened it (fire_bridge_windows.h). */
#ifndef FIRE_DISPLAY_COMMON_H
#define FIRE_DISPLAY_COMMON_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "fire_display_c.h"
#include "fire_display_keys.h"

typedef struct FireDisplayQueue {
    FireDisplayEvents events;   /* positions in pixels of the window */
    int quit;                   /* a FIRE_EV_CLOSE went in: the window is gone */
    /* a drop that is going on: the paths so far (one per line) and the text; whether a hover was reported and where */
    char* dropFiles;
    char* dropText;
    int dropping, hovering;
    float hoverX, hoverY;
    int buttons;                /* the mouse buttons that are down (bit button - 1) */
    float mouseX, mouseY;       /* where the mouse was last (for the relative motion) */
    int mouseKnown;
} FireDisplayQueue;

static void fire_display_queueInit(FireDisplayQueue* q) { memset(q, 0, sizeof *q); }
static void fire_display_queueFree(FireDisplayQueue* q) {
    fire_display_eventsFree(&q->events);
    free(q->dropFiles); free(q->dropText);
    q->dropFiles = q->dropText = NULL;
    q->dropping = q->hovering = 0;
}

/* Puts an event into the queue; its `data` (malloc) belongs to the queue then (fire_display_push keeps a copy of its own). */
static void fire_display_queuePush(FireDisplayQueue* q, const FireDisplayEvent* e) {
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT) || defined(FIRE_DISPLAY_SELFTEST_DROP) || defined(FIRE_DISPLAY_SELFTEST_HOVER)
    /* (the tests compare the events with those of the SDL displays: what a real window adds of its own - the keyboard, the mouse coming in - is left out) */
    if (e->type == FIRE_EV_FOCUS_GAINED || e->type == FIRE_EV_FOCUS_LOST || e->type == FIRE_EV_MOUSE_ENTER || e->type == FIRE_EV_MOUSE_LEAVE || e->type == FIRE_EV_SCALE) { free(e->data); return; }
#endif
    if (e->type == FIRE_EV_CLOSE) q->quit = 1;
    fire_display_push(&q->events, e);
    free(e->data);
}
static void fire_display_queueSimple(FireDisplayQueue* q, int type) { FireDisplayEvent e; fire_display_eventInit(&e); e.type = type; fire_display_queuePush(q, &e); }

/* Hands the events to the bridge (`out`), positions converted from pixels of the window (winW x winH) to pixels of the framebuffer (fbW x fbH). The queue is empty after. */
static void fire_display_queueTake(FireDisplayQueue* q, FireDisplayEvents* out, int winW, int winH, int fbW, int fbH) {
    size_t i;
    float sx = (winW > 0 && fbW > 0) ? (float)fbW / (float)winW : 1.0f;
    float sy = (winH > 0 && fbH > 0) ? (float)fbH / (float)winH : 1.0f;
    for (i = 0; i < q->events.count; i++) {
        FireDisplayEvent e = q->events.items[i];
        switch (e.type) {
            case FIRE_EV_MOUSE_DOWN: case FIRE_EV_MOUSE_UP: case FIRE_EV_MOUSE_MOVE: case FIRE_EV_MOUSE_SCROLL: case FIRE_EV_TOUCH_DOWN: case FIRE_EV_TOUCH_MOVE:
            case FIRE_EV_TOUCH_UP: case FIRE_EV_DROP_FILES: case FIRE_EV_DROP_TEXT: case FIRE_EV_DRAG_HOVER:
                e.x *= sx; e.y *= sy;
                break;
            case FIRE_EV_MOUSE_MOVE_REL: e.xrel *= sx; e.yrel *= sy; break;
            default: break;
        }
        fire_display_push(out, &e);   /* (with a copy of `data`) */
    }
    fire_display_eventsClear(&q->events);
}

/* ---- text ---- */

/* Writes a code point as UTF-8 (1-4 bytes; returns how many). */
static int fire_display_utf8(uint32_t c, char* out) {
    if (c < 0x80) { out[0] = (char)c; return 1; }
    if (c < 0x800) { out[0] = (char)(0xC0 | (c >> 6)); out[1] = (char)(0x80 | (c & 0x3F)); return 2; }
    if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
    if (c < 0x10000) { out[0] = (char)(0xE0 | (c >> 12)); out[1] = (char)(0x80 | ((c >> 6) & 0x3F)); out[2] = (char)(0x80 | (c & 0x3F)); return 3; }
    if (c > 0x10FFFF) c = 0xFFFD;
    if (c > 0xFFFF) { out[0] = (char)(0xF0 | (c >> 18)); out[1] = (char)(0x80 | ((c >> 12) & 0x3F)); out[2] = (char)(0x80 | ((c >> 6) & 0x3F)); out[3] = (char)(0x80 | (c & 0x3F)); return 4; }
    return fire_display_utf8(0xFFFD, out);
}

/* UTF-16 (n units) as UTF-8 (malloc, terminated; NULL without memory). */
static char* fire_display_utf16To8(const uint16_t* s, size_t n) {
    char* out = (char*)malloc(n * 3 + 1);
    size_t i, k = 0;
    if (out == NULL) return NULL;
    for (i = 0; i < n; i++) {
        uint32_t c = s[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) { c = 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00); i++; }
        k += (size_t)fire_display_utf8(c, out + k);
    }
    out[k] = 0;
    return out;
}

/* Typed (or composed) text: FIRE_EV_TEXT_INPUT events of at most 31 bytes each, cut between characters. Control characters (below 32, 127) are left out: they come as keys. */
static void fire_display_queueText(FireDisplayQueue* q, const char* text, size_t n) {
    size_t i = 0;
    while (i < n) {
        FireDisplayEvent e;
        size_t k = 0;
        fire_display_eventInit(&e);
        e.type = FIRE_EV_TEXT_INPUT;
        while (i < n) {
            unsigned char c = (unsigned char)text[i];
            size_t len = c < 0x80 ? 1 : (c >> 5) == 6 ? 2 : (c >> 4) == 14 ? 3 : (c >> 3) == 30 ? 4 : 1;
            if (i + len > n) len = n - i;
            if (c < 32 || c == 127) { i++; continue; }
            if (k + len > sizeof e.text - 1) break;
            memcpy(e.text + k, text + i, len);
            k += len; i += len;
        }
        e.text[k] = 0;
        if (k > 0) fire_display_queuePush(q, &e);
    }
}
static void fire_display_queueCodepoint(FireDisplayQueue* q, uint32_t c) { char buf[4]; int n = fire_display_utf8(c, buf); fire_display_queueText(q, buf, (size_t)n); }

/* The composition of an input method changed: `text` (UTF-8, n bytes; NULL or n == 0: it ended), its cursor and the marked characters after it. */
static void fire_display_queueEditing(FireDisplayQueue* q, const char* text, size_t n, int cursor, int selection) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = FIRE_EV_TEXT_EDITING;
    e.cursor = cursor < 0 ? 0 : cursor;
    e.selection = selection < 0 ? 0 : selection;
    if (text != NULL && n > 0) { e.data = (char*)fire_display_copyBytes(text, n); if (e.data == NULL) return; }
    fire_display_queuePush(q, &e);
}

/* ---- keys and the mouse ---- */

static void fire_display_queueKey(FireDisplayQueue* q, int down, int keyCode, int scanCode, int modifier, int repeat) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = down ? FIRE_EV_KEY_DOWN : FIRE_EV_KEY_UP;
    e.keyCode = keyCode; e.scanCode = scanCode; e.modifier = modifier; e.repeat = repeat ? 1 : 0;
    fire_display_queuePush(q, &e);
}

static void fire_display_queueButton(FireDisplayQueue* q, int down, int button, float x, float y) {
    FireDisplayEvent e;
    if (button < 1 || button > 31) return;
    if (down) q->buttons |= 1 << (button - 1); else q->buttons &= ~(1 << (button - 1));
    fire_display_eventInit(&e);
    e.type = down ? FIRE_EV_MOUSE_DOWN : FIRE_EV_MOUSE_UP;
    e.button = button; e.x = x; e.y = y;
    fire_display_queuePush(q, &e);
}

/* The mouse moved to (x, y): MouseMove and MouseMoveRelative (by how much since the last position; `dx`/`dy` given by the window system when `relKnown`). */
static void fire_display_queueMotion2(FireDisplayQueue* q, float x, float y, int relKnown, float dx, float dy) {
    FireDisplayEvent e, r;
    if (!relKnown) { dx = q->mouseKnown ? x - q->mouseX : 0.0f; dy = q->mouseKnown ? y - q->mouseY : 0.0f; }
    q->mouseX = x; q->mouseY = y; q->mouseKnown = 1;
    fire_display_eventInit(&e);
    e.type = FIRE_EV_MOUSE_MOVE; e.x = x; e.y = y; e.buttonState = q->buttons;
    fire_display_queuePush(q, &e);
    fire_display_eventInit(&r);
    r.type = FIRE_EV_MOUSE_MOVE_REL; r.xrel = dx; r.yrel = dy; r.buttonState = q->buttons;
    fire_display_queuePush(q, &r);
}
static void fire_display_queueMotion(FireDisplayQueue* q, float x, float y) { fire_display_queueMotion2(q, x, y, 0, 0.0f, 0.0f); }

/* The wheel: (dx, dy) in notches (positive y: away from the user), where the mouse is. */
static void fire_display_queueScroll(FireDisplayQueue* q, float dx, float dy, float x, float y) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = FIRE_EV_MOUSE_SCROLL; e.scrollX = dx; e.scrollY = dy; e.x = x; e.y = y;
    fire_display_queuePush(q, &e);
}

static void fire_display_queueTouch(FireDisplayQueue* q, int type, long long finger, float x, float y, float pressure) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = type; e.finger = finger; e.x = x; e.y = y; e.pressure = pressure;
    fire_display_queuePush(q, &e);
}

static void fire_display_queueResize(FireDisplayQueue* q, int width, int height) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = FIRE_EV_RESIZE; e.width = width; e.height = height;
    fire_display_queuePush(q, &e);
}

static void fire_display_queueScale(FireDisplayQueue* q, double scale) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = FIRE_EV_SCALE; e.value = (float)scale;
    fire_display_queuePush(q, &e);
}

static void fire_display_queueJoy(FireDisplayQueue* q, int type, int joystick, int index, float value) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = type; e.joystick = joystick; e.index = index; e.value = value;
    fire_display_queuePush(q, &e);
}

/* ---- drops: what another program drags over the window and lets go of ---- */

/* (appends a line to a text that grows: "a" + "b" = "a\nb") */
static char* fire_display_addLine(char* all, const char* line, size_t n) {
    size_t a = all ? strlen(all) : 0;
    char* grown = (char*)realloc(all, a + n + 2);
    if (grown == NULL) return all;
    if (a > 0) grown[a++] = '\n';
    memcpy(grown + a, line, n);
    grown[a + n] = 0;
    return grown;
}

/* Something of another program is over the window at (x, y): DragHover, only when it moved. */
static void fire_display_dropHover(FireDisplayQueue* q, float x, float y) {
    q->dropping = 1;
    if (!q->hovering || x != q->hoverX || y != q->hoverY) {
        FireDisplayEvent e;
        q->hovering = 1; q->hoverX = x; q->hoverY = y;
        fire_display_eventInit(&e);
        e.type = FIRE_EV_DRAG_HOVER; e.x = x; e.y = y;
        fire_display_queuePush(q, &e);
    }
}
static void fire_display_dropFile(FireDisplayQueue* q, const char* path, size_t n) { q->dropFiles = fire_display_addLine(q->dropFiles, path, n); }
static void fire_display_dropText(FireDisplayQueue* q, const char* text, size_t n) { q->dropText = fire_display_addLine(q->dropText, text, n); }

/* The drop is over (at x, y): DropFiles with the paths, DropText with the text; a drag that brought nothing (it left the window) ends with DragLeave. */
static void fire_display_dropDone(FireDisplayQueue* q, float x, float y) {
    FireDisplayEvent e;
    int brought = 0;
    if (q->dropFiles != NULL) {
        fire_display_eventInit(&e);
        e.type = FIRE_EV_DROP_FILES; e.x = x; e.y = y; e.data = q->dropFiles;
        q->dropFiles = NULL;
        fire_display_queuePush(q, &e);
        brought = 1;
    }
    if (q->dropText != NULL) {
        fire_display_eventInit(&e);
        e.type = FIRE_EV_DROP_TEXT; e.x = x; e.y = y; e.data = q->dropText;
        q->dropText = NULL;
        fire_display_queuePush(q, &e);
        brought = 1;
    }
    if (!brought && q->hovering) fire_display_queueSimple(q, FIRE_EV_DRAG_LEAVE);
    q->dropping = 0;
    q->hovering = 0;
}
/* The drag left the window without being dropped. */
static void fire_display_dropLeave(FireDisplayQueue* q) {
    free(q->dropFiles); free(q->dropText);
    q->dropFiles = q->dropText = NULL;
    if (q->hovering) fire_display_queueSimple(q, FIRE_EV_DRAG_LEAVE);
    q->dropping = 0;
    q->hovering = 0;
}

/* ---- the events of the tests ---- */
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT) || defined(FIRE_DISPLAY_SELFTEST_DROP) || defined(FIRE_DISPLAY_SELFTEST_HOVER)
#define FIRE_DISPLAY_HAS_SELFTEST 1
/* The tests (and nobody else) get a fixed series of events when a window opens (winW x winH): keys, a click, motion, the wheel, text and the close - the same events,
 * in the same order and with the same values, as the SDL displays gave (fire_display_sdl3.h), so that the output of the tests stays what it was. */
static void fire_display_selftest(FireDisplayQueue* q, int winW, int winH) {
    FireDisplayEvent e;
    (void)winW; (void)winH;
#ifdef FIRE_DISPLAY_SELFTEST_RESIZE
    /* (only for the test of Window.AutoResize: the window is pulled to 90x70) */
    if (winW != 90 || winH != 70) fire_display_queueResize(q, 90, 70);
    return;
#endif
#ifdef FIRE_DISPLAY_SELFTEST_INPUT
    /* (only for the test of touch and joystick: a finger touches, moves and lifts; a joystick moves an axis, presses a button, moves the hat, is removed) */
    fire_display_queueTouch(q, FIRE_EV_TOUCH_DOWN, 5, 0.5f * (float)winW, 0.25f * (float)winH, 1.0f);
    fire_display_queueTouch(q, FIRE_EV_TOUCH_MOVE, 5, 0.75f * (float)winW, 0.5f * (float)winH, 0.5f);
    fire_display_queueTouch(q, FIRE_EV_TOUCH_UP, 5, 0.75f * (float)winW, 0.5f * (float)winH, 0.0f);
    fire_display_queueJoy(q, FIRE_EV_JOY_AXIS, 7, 1, -1.0f);
    fire_display_queueJoy(q, FIRE_EV_JOY_BUTTON_DOWN, 7, 3, 0.0f);
    fire_display_queueJoy(q, FIRE_EV_JOY_BUTTON_UP, 7, 3, 0.0f);
    fire_display_queueJoy(q, FIRE_EV_JOY_HAT, 7, 0, 3.0f);
    fire_display_queueJoy(q, FIRE_EV_JOY_REMOVED, 7, 0, 0.0f);
    return;
#endif
#ifdef FIRE_DISPLAY_SELFTEST_HOVER
    /* (only for the test of DragHover/DragLeave: something is dragged over the window and leaves it; then something is dragged over it and dropped) */
    q->dropping = 1;
    fire_display_dropHover(q, 10, 20);
    fire_display_dropHover(q, 10, 20);
    fire_display_dropHover(q, 30, 22);
    fire_display_dropDone(q, 30, 22);
    q->dropping = 1;
    fire_display_dropHover(q, 40, 30);
    fire_display_dropFile(q, "/tmp/hier.txt", 13);
    fire_display_dropDone(q, 40, 30);
    return;
#endif
    fire_display_queueKey(q, 1, 'a', FIRE_SC_A, FIRE_MOD_LSHIFT, 0);
    fire_display_queueKey(q, 0, 'a', FIRE_SC_A, 0, 0);
    fire_display_queueButton(q, 1, FIRE_BUTTON_LEFT, 32, 24);
    fire_display_queueButton(q, 0, FIRE_BUTTON_LEFT, 32, 24);
    fire_display_eventInit(&e); e.type = FIRE_EV_MOUSE_MOVE; e.x = 10; e.y = 12; e.buttonState = 1; fire_display_queuePush(q, &e);
    fire_display_eventInit(&e); e.type = FIRE_EV_MOUSE_MOVE_REL; e.xrel = 3; e.yrel = -2; e.buttonState = 1; fire_display_queuePush(q, &e);
    fire_display_queueScroll(q, 0, 1.5f, 0, 0);
    fire_display_queueText(q, "\xC3\xA9x", 3);
    fire_display_queueSimple(q, FIRE_EV_CLOSE_REQUEST);
#ifdef FIRE_DISPLAY_SELFTEST_DROP
    /* two files and a text are dropped at (40, 30) of the window; then a single file without begin/complete */
    q->dropping = 1;
    fire_display_dropFile(q, "/tmp/eins.txt", 13);
    fire_display_dropFile(q, "/tmp/zwei \xC3\xA4.png", 16);
    fire_display_dropText(q, "gezogener Text", 14);
    fire_display_dropDone(q, 40, 30);
    fire_display_dropFile(q, "/tmp/allein.txt", 15);
    fire_display_dropDone(q, 40, 30);
#endif
    fire_display_queueSimple(q, FIRE_EV_CLOSE);
}
#endif

#endif
