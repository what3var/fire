/* fire i2c package: I2C part in C (the HAL of the i2c bridge): what the implementations share - the error codes of the language, the status of an
 * operation, a list of names.
 *
 * The i2c package brings, one file per platform (platformSources of i2c.fireproj),:
 *   enum FIRE_I2C_E_*                the codes of the error slot (the same numbers as in the prelude: I2c.I2cException.code; below)
 *   FireI2cStatus { code; message }  code 0 = ok
 *   typedef struct FireI2cBus FireI2cBus;   an opened bus (opaque)
 *   int  fire_i2c_supported(void)                           0: there is no I2C here (the list of buses is empty)
 *   int  fire_i2c_buses(FireI2cList* names, st)             the buses of this machine ("i2c-1" on Linux, "i2c-0" on the ESP32)
 *   FireI2cBus* fire_i2c_openBus(name, int speedHz, st)     NotFound for a bus that is not there; NULL on an error
 *   int  fire_i2c_setSpeed(FireI2cBus*, int speedHz, st)    where the software decides (the ESP32); on Linux the device tree does, the call is accepted and the
 *                                                           value ignored
 *   int  fire_i2c_write(FireI2cBus*, int address, const uint8_t*, int n, st)   7-bit address; NoAck if nobody answers
 *   int  fire_i2c_read(FireI2cBus*, int address, uint8_t*, int n, st)
 *   int  fire_i2c_writeRead(FireI2cBus*, int address, const uint8_t* out, int nout, uint8_t* in, int nin, st)   with a repeated start in between (no stop)
 *   int  fire_i2c_probe(FireI2cBus*, int address, int* present, st)           is somebody there
 *   void fire_i2c_closeBus(FireI2cBus*)
 * The functions that return int give 1 when done and 0 on an error (the status says why). Nothing waits long: a transfer ends when the bus is done with it or its
 * time (a few 100 ms) runs out (Timeout). A board with I2C buses of its own provides its own `fire_i2c.h`. */
#ifndef FIRE_I2C_C_H
#define FIRE_I2C_C_H

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The error codes of the language (the same numbers as in the prelude of the i2c package: I2c.I2cException.code). */
enum { FIRE_I2C_E_NONE = 0, FIRE_I2C_E_INVALID_ARGUMENT = 1, FIRE_I2C_E_INVALID_HANDLE = 2, FIRE_I2C_E_NOT_FOUND = 3, FIRE_I2C_E_BUSY = 4, FIRE_I2C_E_PERMISSION = 5,
       FIRE_I2C_E_UNSUPPORTED = 6, FIRE_I2C_E_OTHER = 7, FIRE_I2C_E_NO_ACK = 8, FIRE_I2C_E_TIMEOUT = 9 };

typedef struct FireI2cStatus { int code; char message[176]; } FireI2cStatus;

/* An opened bus: each implementation defines `struct FireI2cBus`. */
typedef struct FireI2cBus FireI2cBus;

/* Records a failure (printf format): always 0. */
static int fire_i2c_fail(FireI2cStatus* st, int code, const char* format, ...) {
    va_list args;
    st->code = code;
    va_start(args, format);
    vsnprintf(st->message, sizeof st->message, format, args);
    va_end(args);
    return 0;
}
static int fire_i2c_success(FireI2cStatus* st) { st->code = 0; st->message[0] = 0; return 1; }

/* ---- a list of names ---- */
typedef struct FireI2cList { char** items; size_t count, cap; } FireI2cList;
static void fire_i2c_listAdd(FireI2cList* l, const char* s) {
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
static void fire_i2c_listFree(FireI2cList* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}

#endif
