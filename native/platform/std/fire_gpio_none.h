/* fire native platform layer, GPIO part in C for a platform without GPIO pins (Windows, macOS, a board without a package of its own, a Linux without the GPIO
 * character device): the list of chips is empty and every call fails with "not supported". The simulated chip `sim` of the bridge works everywhere. A platform
 * package offers `fire_gpio.h` (the generated file defines FIRE_PLATFORM_GPIO_C_HEADER for it) with the functions listed in fire_gpio_c.h. */
#ifndef FIRE_GPIO_NONE_H
#define FIRE_GPIO_NONE_H

#include "fire_gpio_c.h"

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#elif defined(__unix__) || defined(__APPLE__)
#include <time.h>
#endif

struct FireGpioLine { int unused; };

static int fire_gpio_supported(void) { return 0; }
static int fire_gpio_none(FireGpioStatus* st) { return fire_gpio_fail(st, FIRE_GPIO_E_UNSUPPORTED, "This platform has no GPIO pins (the simulated chip \"sim\" works everywhere)."); }

/* (a clock that only goes forward: the edges of the simulated chip) */
static int64_t fire_gpio_nowMicros(void) {
#if defined(_WIN32)
    LARGE_INTEGER count, frequency;
    QueryPerformanceCounter(&count);
    QueryPerformanceFrequency(&frequency);
    return (int64_t)(count.QuadPart / frequency.QuadPart) * 1000000 + (int64_t)(count.QuadPart % frequency.QuadPart) * 1000000 / frequency.QuadPart;
#elif (defined(__unix__) || defined(__APPLE__)) && defined(CLOCK_MONOTONIC)
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (int64_t)t.tv_sec * 1000000 + t.tv_nsec / 1000;
#else
    return fire_plat_nowMs() * 1000;
#endif
}

static int fire_gpio_chips(FireGpioList* names, FireGpioStatus* st) { fire_gpio_listFree(names); return fire_gpio_success(st); }
static FireGpioLine* fire_gpio_openLine(const char* chip, int line, FireGpioStatus* st) {
    (void)chip; (void)line;
    fire_gpio_fail(st, FIRE_GPIO_E_NOT_FOUND, "There is no such GPIO chip on this platform.");
    return NULL;
}
static int fire_gpio_configure(FireGpioLine* l, int direction, int pull, int edge, int value, FireGpioStatus* st) {
    (void)l; (void)direction; (void)pull; (void)edge; (void)value; return fire_gpio_none(st);
}
static int fire_gpio_read(FireGpioLine* l, int* value, FireGpioStatus* st) { (void)l; *value = 0; return fire_gpio_none(st); }
static int fire_gpio_write(FireGpioLine* l, int value, FireGpioStatus* st) { (void)l; (void)value; return fire_gpio_none(st); }
static int fire_gpio_pollEdge(FireGpioLine* l, int* event, int64_t* micros, FireGpioStatus* st) { (void)l; *event = 0; *micros = 0; return fire_gpio_none(st); }
static void fire_gpio_closeLine(FireGpioLine* l) { (void)l; }

#endif
