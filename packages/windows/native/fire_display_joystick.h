/* fire native platform package "std": joysticks and gamepads for fire's own displays (fire_display_desktop.h).
 *
 * Every pump of a window asks the devices (fire_joy_poll): what changed since the last time comes as FIRE_EV_JOY_* events - to the window
 * that pumps first (the devices belong to the program, not to a window). A device that is there when the first window pumps reports itself with FIRE_EV_JOY_ADDED.
 * Axes go from -1 to 1, buttons are numbered from 0, a hat is a mask (1 up, 2 right, 4 down, 8 left); a device keeps its number while it is plugged in.
 *   Linux    the joystick devices of the kernel (/dev/input/js*: axes, buttons; axes that are a hat - ABS_HAT* in the axis map - as hats), looked for again every second
 *   Windows  XInput (xinput1_4.dll or xinput9_1_0.dll, loaded at run time): four gamepads - axes left x/y, left trigger, right x/y, right trigger; ten buttons; the pad as a hat
 *   macOS    the GameController framework (its extended gamepads, the same order as XInput)
 *   browser  navigator.getGamepads()
 * The tests (FIRE_DISPLAY_SELFTEST*) and the display without a screen ask no devices. */
#ifndef FIRE_DISPLAY_JOYSTICK_H
#define FIRE_DISPLAY_JOYSTICK_H

#include <string.h>
#include "fire_display_c.h"

#define FIRE_JOY_MAX 8
#define FIRE_JOY_AXES 16
#define FIRE_JOY_HATS 4

typedef struct FireJoyState {
    int connected, id;
    int axisCount, buttonCount, hatCount;
    float axes[FIRE_JOY_AXES];
    uint32_t buttons;
    int hats[FIRE_JOY_HATS];
} FireJoyState;

typedef struct FireJoySlot {
    FireJoyState now, before;
    int reported;
#if defined(__linux__) && !defined(__EMSCRIPTEN__)
    int fd;
    unsigned char hatAxis[64];   /* axis -> 1 + hat * 2 + (0: x, 1: y), 0: a plain axis */
    unsigned char axisIndex[64]; /* axis of the device -> axis reported */
#endif
} FireJoySlot;

static FireJoySlot g_fireJoy[FIRE_JOY_MAX];
static int g_fireJoyNextId = 0;
static int g_fireJoyReady = 0;

static void fire_joy_emit(FireDisplayEvents* out, int type, int joystick, int index, float value) {
    FireDisplayEvent e;
    fire_display_eventInit(&e);
    e.type = type; e.joystick = joystick; e.index = index; e.value = value;
    fire_display_push(out, &e);
}

/* What changed in a slot since the last poll: the events. */
static void fire_joy_diff(FireJoySlot* s, FireDisplayEvents* out) {
    int k;
    if (s->now.connected && !s->reported) {
        s->now.id = g_fireJoyNextId++;
        s->reported = 1;
        fire_joy_emit(out, FIRE_EV_JOY_ADDED, s->now.id, 0, 0);
        memset(s->before.axes, 0, sizeof s->before.axes);
        s->before.buttons = 0;
        memset(s->before.hats, 0, sizeof s->before.hats);
    }
    if (!s->now.connected) {
        if (s->reported) fire_joy_emit(out, FIRE_EV_JOY_REMOVED, s->before.id, 0, 0);
        s->reported = 0;
        memset(&s->before, 0, sizeof s->before);
        return;
    }
    for (k = 0; k < s->now.axisCount && k < FIRE_JOY_AXES; k++)
        if (s->now.axes[k] != s->before.axes[k]) fire_joy_emit(out, FIRE_EV_JOY_AXIS, s->now.id, k, s->now.axes[k]);
    for (k = 0; k < s->now.buttonCount && k < 32; k++) {
        uint32_t bit = 1u << k;
        if ((s->now.buttons & bit) != (s->before.buttons & bit)) fire_joy_emit(out, (s->now.buttons & bit) ? FIRE_EV_JOY_BUTTON_DOWN : FIRE_EV_JOY_BUTTON_UP, s->now.id, k, 0);
    }
    for (k = 0; k < s->now.hatCount && k < FIRE_JOY_HATS; k++)
        if (s->now.hats[k] != s->before.hats[k]) fire_joy_emit(out, FIRE_EV_JOY_HAT, s->now.id, k, (float)s->now.hats[k]);
    s->before = s->now;
}

static float fire_joy_axis16(int v) { float f = (float)v / 32767.0f; return f < -1.0f ? -1.0f : f > 1.0f ? 1.0f : f; }

/* ================================================================================================================================================ */
#if defined(__EMSCRIPTEN__)
#include <emscripten.h>
/* (the browser: one pad into ints/floats - [connected, axes, buttons mask, button count], the axes) */
EM_JS(int, fire_joyjs_read, (int index, int* info, float* axes), {
    var pads = navigator.getGamepads ? navigator.getGamepads() : [];
    var p = pads && pads[index];
    if (!p || !p.connected) { HEAP32[info >> 2] = 0; return 0; }
    var n = Math.min(p.axes.length, 16), mask = 0, k;
    for (k = 0; k < n; k++) HEAPF32[(axes >> 2) + k] = p.axes[k];
    for (k = 0; k < Math.min(p.buttons.length, 32); k++) if (p.buttons[k].pressed) mask |= 1 << k;
    HEAP32[info >> 2] = 1; HEAP32[(info >> 2) + 1] = n; HEAP32[(info >> 2) + 2] = mask; HEAP32[(info >> 2) + 3] = Math.min(p.buttons.length, 32);
    return 1;
});
static void fire_joy_read(double now) {
    int i;
    (void)now;
    for (i = 0; i < 4 && i < FIRE_JOY_MAX; i++) {
        int info[4] = { 0, 0, 0, 0 };
        FireJoyState* s = &g_fireJoy[i].now;
        fire_joyjs_read(i, info, s->axes);
        s->connected = info[0];
        s->axisCount = info[1]; s->buttons = (uint32_t)info[2]; s->buttonCount = info[3]; s->hatCount = 0;
    }
}
static double fire_joy_now(void) { return emscripten_get_now() / 1000.0; }

/* ================================================================================================================================================ */
#elif defined(_WIN32)
typedef struct FireXInputPad { WORD buttons; BYTE lt, rt; SHORT lx, ly, rx, ry; } FireXInputPad;
typedef struct FireXInputState { DWORD packet; FireXInputPad pad; } FireXInputState;
static DWORD (WINAPI *g_fireXInputGetState)(DWORD, FireXInputState*);
static double fire_joy_now(void) { static LARGE_INTEGER f; LARGE_INTEGER c; if (f.QuadPart == 0) QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c); return (double)c.QuadPart / (double)f.QuadPart; }
static void fire_joy_read(double now) {
    static int tried = 0;
    static double lastMissing[4] = { -10, -10, -10, -10 };
    DWORD i;
    if (!tried) {
        HMODULE x;
        FARPROC p;
        tried = 1;
        x = LoadLibraryW(L"xinput1_4.dll");
        if (x == NULL) x = LoadLibraryW(L"xinput9_1_0.dll");
        if (x == NULL) x = LoadLibraryW(L"xinput1_3.dll");
        p = x ? GetProcAddress(x, "XInputGetState") : NULL;
        memcpy(&g_fireXInputGetState, &p, sizeof p);
    }
    if (g_fireXInputGetState == NULL) return;
    for (i = 0; i < 4; i++) {
        FireXInputState st;
        FireJoyState* s = &g_fireJoy[i].now;
        /* (asking a slot without a pad is slow: once a second) */
        if (!s->connected && now - lastMissing[i] < 1.0) continue;
        memset(&st, 0, sizeof st);
        if (g_fireXInputGetState(i, &st) != 0) { s->connected = 0; lastMissing[i] = now; continue; }
        s->connected = 1;
        s->axisCount = 6; s->buttonCount = 10; s->hatCount = 1;
        s->axes[0] = fire_joy_axis16(st.pad.lx); s->axes[1] = -fire_joy_axis16(st.pad.ly);
        s->axes[2] = (float)st.pad.lt / 127.5f - 1.0f;
        s->axes[3] = fire_joy_axis16(st.pad.rx); s->axes[4] = -fire_joy_axis16(st.pad.ry);
        s->axes[5] = (float)st.pad.rt / 127.5f - 1.0f;
        {
            static const WORD order[10] = { 0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0x0020, 0x0010, 0x0040, 0x0080 };   /* A B X Y LB RB Back Start LS RS */
            int k;
            s->buttons = 0;
            for (k = 0; k < 10; k++) if (st.pad.buttons & order[k]) s->buttons |= 1u << k;
        }
        s->hats[0] = ((st.pad.buttons & 0x1) ? 1 : 0) | ((st.pad.buttons & 0x8) ? 2 : 0) | ((st.pad.buttons & 0x2) ? 4 : 0) | ((st.pad.buttons & 0x4) ? 8 : 0);
    }
}

/* ================================================================================================================================================ */
#elif defined(__APPLE__)
/* GameController through the Objective-C runtime (fire_display_cocoa.h has its declarations) */
static double fire_joy_now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + (double)t.tv_nsec * 1e-9; }
static float fire_joy_value(FireId element, const char* axis) { FireId a = FIRE_NS0(FireId, element, axis); return a ? FIRE_NS0(float, a, "value") : 0.0f; }
static int fire_joy_pressed(FireId pad, const char* name) { FireId b = FIRE_NS0(FireId, pad, name); return b ? FIRE_NS0(signed char, b, "isPressed") != 0 : 0; }
static void fire_joy_read(double now) {
    void* pool;
    FireId list;
    unsigned long n, i;
    (void)now;
    if (objc_getClass("GCController") == NULL) return;
    pool = objc_autoreleasePoolPush();
    list = FIRE_NS0(FireId, FIRE_CLS("GCController"), "controllers");
    n = list ? FIRE_NS0(unsigned long, list, "count") : 0;
    for (i = 0; i < FIRE_JOY_MAX; i++) {
        FireJoyState* s = &g_fireJoy[i].now;
        FireId c = i < n ? FIRE_NS(FireId, (FireId, FireSel, unsigned long), list, "objectAtIndex:", i) : NULL;
        FireId pad = c ? FIRE_NS0(FireId, c, "extendedGamepad") : NULL;
        if (pad == NULL) { s->connected = 0; continue; }
        s->connected = 1;
        s->axisCount = 6; s->buttonCount = 10; s->hatCount = 1;
        s->axes[0] = fire_joy_value(FIRE_NS0(FireId, pad, "leftThumbstick"), "xAxis");
        s->axes[1] = -fire_joy_value(FIRE_NS0(FireId, pad, "leftThumbstick"), "yAxis");
        s->axes[2] = FIRE_NS0(float, FIRE_NS0(FireId, pad, "leftTrigger"), "value") * 2.0f - 1.0f;
        s->axes[3] = fire_joy_value(FIRE_NS0(FireId, pad, "rightThumbstick"), "xAxis");
        s->axes[4] = -fire_joy_value(FIRE_NS0(FireId, pad, "rightThumbstick"), "yAxis");
        s->axes[5] = FIRE_NS0(float, FIRE_NS0(FireId, pad, "rightTrigger"), "value") * 2.0f - 1.0f;
        s->buttons = (uint32_t)(fire_joy_pressed(pad, "buttonA") | fire_joy_pressed(pad, "buttonB") << 1 | fire_joy_pressed(pad, "buttonX") << 2 | fire_joy_pressed(pad, "buttonY") << 3
                     | fire_joy_pressed(pad, "leftShoulder") << 4 | fire_joy_pressed(pad, "rightShoulder") << 5 | fire_joy_pressed(pad, "buttonOptions") << 6
                     | fire_joy_pressed(pad, "buttonMenu") << 7 | fire_joy_pressed(pad, "leftThumbstickButton") << 8 | fire_joy_pressed(pad, "rightThumbstickButton") << 9);
        {
            FireId d = FIRE_NS0(FireId, pad, "dpad");
            s->hats[0] = (fire_joy_pressed(d, "up") ? 1 : 0) | (fire_joy_pressed(d, "right") ? 2 : 0) | (fire_joy_pressed(d, "down") ? 4 : 0) | (fire_joy_pressed(d, "left") ? 8 : 0);
        }
    }
    objc_autoreleasePoolPop(pool);
}

/* ================================================================================================================================================ */
#elif defined(__linux__)
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <sys/ioctl.h>
#include <time.h>
#include <unistd.h>
#include <linux/joystick.h>
static double fire_joy_now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (double)t.tv_sec + (double)t.tv_nsec * 1e-9; }
static double g_fireJoyLastScan = -10.0;
static void fire_joy_open(FireJoySlot* s, int fd) {
    unsigned char axes = 0, buttons = 0, map[ABS_CNT];
    int k, plain = 0;
    memset(&s->now, 0, sizeof s->now);
    memset(map, 0, sizeof map);
    ioctl(fd, JSIOCGAXES, &axes);
    ioctl(fd, JSIOCGBUTTONS, &buttons);
    ioctl(fd, JSIOCGAXMAP, map);
    s->fd = fd;
    memset(s->hatAxis, 0, sizeof s->hatAxis);
    for (k = 0; k < axes && k < 64; k++) {
        /* ABS_HAT0X .. ABS_HAT3Y (0x10 - 0x17): a hat of its own */
        if (map[k] >= ABS_HAT0X && map[k] <= ABS_HAT3Y && (map[k] - ABS_HAT0X) / 2 < FIRE_JOY_HATS) {
            int hat = (map[k] - ABS_HAT0X) / 2;
            s->hatAxis[k] = (unsigned char)(1 + hat * 2 + ((map[k] - ABS_HAT0X) & 1));
            if (hat + 1 > s->now.hatCount) s->now.hatCount = hat + 1;
        } else if (plain < FIRE_JOY_AXES) s->axisIndex[k] = (unsigned char)plain++;
        else s->axisIndex[k] = 255;
    }
    s->now.axisCount = plain;
    s->now.buttonCount = buttons > 32 ? 32 : buttons;
    s->now.connected = 1;
}
static void fire_joy_read(double now) {
    int i;
    /* new devices: every second */
    if (now - g_fireJoyLastScan >= 1.0) {
        g_fireJoyLastScan = now;
        for (i = 0; i < FIRE_JOY_MAX; i++) {
            char path[32];
            int fd;
            if (g_fireJoy[i].fd > 0) continue;
            snprintf(path, sizeof path, "/dev/input/js%d", i);
            fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
            if (fd >= 0) fire_joy_open(&g_fireJoy[i], fd);
        }
    }
    for (i = 0; i < FIRE_JOY_MAX; i++) {
        FireJoySlot* s = &g_fireJoy[i];
        struct js_event e;
        ssize_t got;
        if (s->fd <= 0) continue;
        while ((got = read(s->fd, &e, sizeof e)) == (ssize_t)sizeof e) {
            int n = e.number;
            if ((e.type & ~JS_EVENT_INIT) == JS_EVENT_BUTTON && n < 32) { if (e.value) s->now.buttons |= 1u << n; else s->now.buttons &= ~(1u << n); }
            else if ((e.type & ~JS_EVENT_INIT) == JS_EVENT_AXIS && n < 64) {
                if (s->hatAxis[n]) {
                    int hat = (s->hatAxis[n] - 1) / 2, y = (s->hatAxis[n] - 1) & 1, h = s->now.hats[hat];
                    if (y) h = (h & ~(1 | 4)) | (e.value < 0 ? 1 : e.value > 0 ? 4 : 0);
                    else h = (h & ~(2 | 8)) | (e.value > 0 ? 2 : e.value < 0 ? 8 : 0);
                    s->now.hats[hat] = h;
                } else if (s->axisIndex[n] < FIRE_JOY_AXES) s->now.axes[s->axisIndex[n]] = fire_joy_axis16(e.value);
            }
        }
        if (got < 0 && errno != EAGAIN && errno != EINTR) { close(s->fd); s->fd = 0; s->now.connected = 0; }   /* (unplugged) */
    }
}

#else
static double fire_joy_now(void) { return 0; }
static void fire_joy_read(double now) { (void)now; }
#endif

/* (windows are pumped on several threads: one of them asks the devices at a time) */
#if defined(_WIN32)
static SRWLOCK g_fireJoyLock = SRWLOCK_INIT;
static void fire_joy_lock(void) { AcquireSRWLockExclusive(&g_fireJoyLock); }
static void fire_joy_unlock(void) { ReleaseSRWLockExclusive(&g_fireJoyLock); }
#elif defined(__EMSCRIPTEN__)
static void fire_joy_lock(void) {}
static void fire_joy_unlock(void) {}
#else
#include <pthread.h>
static pthread_mutex_t g_fireJoyLock = PTHREAD_MUTEX_INITIALIZER;
static void fire_joy_lock(void) { pthread_mutex_lock(&g_fireJoyLock); }
static void fire_joy_unlock(void) { pthread_mutex_unlock(&g_fireJoyLock); }
#endif

/* Asks the devices and appends what changed to `out` (only where real devices are asked: not in the tests, not without a screen). */
static void fire_joy_poll(FireDisplayEvents* out, int real) {
    int i;
#if defined(FIRE_DISPLAY_SELFTEST) || defined(FIRE_DISPLAY_SELFTEST_RESIZE) || defined(FIRE_DISPLAY_SELFTEST_INPUT) || defined(FIRE_DISPLAY_SELFTEST_DROP) || defined(FIRE_DISPLAY_SELFTEST_HOVER) || defined(FIRE_NO_JOYSTICK)
    (void)out; (void)real; (void)i; (void)g_fireJoyReady; (void)fire_joy_diff; (void)fire_joy_read; (void)fire_joy_now; (void)fire_joy_lock; (void)fire_joy_unlock;
    return;
#else
    if (!real) return;
    fire_joy_lock();
    if (!g_fireJoyReady) { g_fireJoyReady = 1; memset(g_fireJoy, 0, sizeof g_fireJoy); }
    fire_joy_read(fire_joy_now());
    for (i = 0; i < FIRE_JOY_MAX; i++) fire_joy_diff(&g_fireJoy[i], out);
    fire_joy_unlock();
#endif
}

#endif
