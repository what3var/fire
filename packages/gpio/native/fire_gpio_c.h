/* fire gpio package: GPIO part in C (the HAL of the gpio bridge): what the implementations share - the error codes of the language, the status of an
 * operation, a list of names.
 *
 * The gpio package brings, one file per platform (platformSources of gpio.fireproj),:
 *   enum FIRE_GPIO_E_*               the codes of the error slot (the same numbers as in the prelude: Gpio.GpioException.code; below)
 *   FireGpioStatus { code; message } code 0 = ok
 *   typedef struct FireGpioLine FireGpioLine;   an opened pin (opaque)
 *   int  fire_gpio_supported(void)                          0: there is no GPIO here (the chips list is empty)
 *   int  fire_gpio_chips(FireGpioList* names, st)           the chips of this machine/board ("gpiochip0" on Linux, "gpio" on the ESP32)
 *   FireGpioLine* fire_gpio_openLine(chip, int line, st)    claims nothing yet (the first configure does); NotFound for a chip or line that is not there;
 *                                                           NULL on an error
 *   int  fire_gpio_configure(FireGpioLine*, int direction, int pull, int edge, int value, st)   direction 0 input, 1 output; pull 0 none, 1 up, 2 down; edge
 *                                                           0 none, 1 rising, 2 falling, 3 both (inputs); value: the level an output starts with. Can be
 *                                                           called again to change the setup.
 *   int  fire_gpio_read(FireGpioLine*, int* value, st) / fire_gpio_write(FireGpioLine*, int value, st)
 *   int  fire_gpio_pollEdge(FireGpioLine*, int* event, int64_t* micros, st)   without waiting: event 0 nothing, 1 rising, 2 falling; micros: when (a clock of
 *                                                           the system)
 *   void fire_gpio_closeLine(FireGpioLine*)                 gives the pin back
 *   int64_t fire_gpio_nowMicros(void)                       a clock that only goes forward, in microseconds (the edges of the simulated chip of the bridge;
 *                                                           the clock of the edge times of pollEdge where there is one)
 * The functions that return int give 1 when done and 0 on an error (the status says why). A board with GPIO pins of its own provides its own `fire_gpio.h`. */
#ifndef FIRE_GPIO_C_H
#define FIRE_GPIO_C_H

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The error codes of the language (the same numbers as in the prelude of the gpio package: Gpio.GpioException.code). */
enum { FIRE_GPIO_E_NONE = 0, FIRE_GPIO_E_INVALID_ARGUMENT = 1, FIRE_GPIO_E_INVALID_HANDLE = 2, FIRE_GPIO_E_NOT_FOUND = 3, FIRE_GPIO_E_BUSY = 4, FIRE_GPIO_E_PERMISSION = 5,
       FIRE_GPIO_E_UNSUPPORTED = 6, FIRE_GPIO_E_OTHER = 7 };

typedef struct FireGpioStatus { int code; char message[176]; } FireGpioStatus;

/* An opened pin: each implementation defines `struct FireGpioLine`. */
typedef struct FireGpioLine FireGpioLine;

/* Records a failure (printf format): always 0. */
static int fire_gpio_fail(FireGpioStatus* st, int code, const char* format, ...) {
    va_list args;
    st->code = code;
    va_start(args, format);
    vsnprintf(st->message, sizeof st->message, format, args);
    va_end(args);
    return 0;
}
static int fire_gpio_success(FireGpioStatus* st) { st->code = 0; st->message[0] = 0; return 1; }

/* ---- a list of names ---- */
typedef struct FireGpioList { char** items; size_t count, cap; } FireGpioList;
static void fire_gpio_listAdd(FireGpioList* l, const char* s) {
    size_t n = strlen(s);
    char* copy;
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        char** grown = (char**)realloc(l->items, sizeof(char*) * cap);
        if (grown == NULL) return;
        l->items = grown;
        l->cap = cap;
    }
    copy = (char*)malloc(n + 1);
    if (copy == NULL) return;
    memcpy(copy, s, n + 1);
    l->items[l->count++] = copy;
}
static void fire_gpio_listFree(FireGpioList* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}

#endif
