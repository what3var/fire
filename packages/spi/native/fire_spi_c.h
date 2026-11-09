/* fire spi package: SPI part in C (the HAL of the spi bridge): what the implementations share - the error codes of the language, the status of an
 * operation, a list of names.
 *
 * The spi package brings, one file per platform (platformSources of spi.fireproj),:
 *   enum FIRE_SPI_E_*                the codes of the error slot (the same numbers as in the prelude: Spi.SpiException.code; below)
 *   FireSpiStatus { code; message }  code 0 = ok
 *   typedef struct FireSpiDevice FireSpiDevice;   an opened device: a bus with one chip select (opaque)
 *   int  fire_spi_supported(void)                           0: there is no SPI here (the list of devices is empty)
 *   int  fire_spi_devices(FireSpiList* names, st)           the devices of this machine ("spidev0.0" on Linux, "spi-2" on the ESP32)
 *   FireSpiDevice* fire_spi_openDevice(name, int mode, int speedHz, int lsbFirst, st)   mode 0..3 (clock polarity and phase); NotFound for a device that is not
 *                                                           there; NULL on an error
 *   int  fire_spi_configure(FireSpiDevice*, int mode, int speedHz, int lsbFirst, st)    changes the setup of an opened device
 *   int  fire_spi_transfer(FireSpiDevice*, const uint8_t* out, uint8_t* in, int n, st)  n bytes full duplex with the chip select held low in between; out == NULL
 *                                                           sends zeros, in == NULL drops what comes in
 *   void fire_spi_closeDevice(FireSpiDevice*)
 * The functions that return int give 1 when done and 0 on an error (the status says why). A transfer is done in the call (n bytes at the speed of the bus; a
 * megabyte at 1 MHz takes 8 seconds - cut long transfers into pieces). A board with SPI of its own provides its own `fire_spi.h`. */
#ifndef FIRE_SPI_C_H
#define FIRE_SPI_C_H

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The error codes of the language (the same numbers as in the prelude of the spi package: Spi.SpiException.code). */
enum { FIRE_SPI_E_NONE = 0, FIRE_SPI_E_INVALID_ARGUMENT = 1, FIRE_SPI_E_INVALID_HANDLE = 2, FIRE_SPI_E_NOT_FOUND = 3, FIRE_SPI_E_BUSY = 4, FIRE_SPI_E_PERMISSION = 5,
       FIRE_SPI_E_UNSUPPORTED = 6, FIRE_SPI_E_OTHER = 7, FIRE_SPI_E_TIMEOUT = 8 };

typedef struct FireSpiStatus { int code; char message[176]; } FireSpiStatus;

/* An opened device: each implementation defines `struct FireSpiDevice`. */
typedef struct FireSpiDevice FireSpiDevice;

/* Records a failure (printf format): always 0. */
static int fire_spi_fail(FireSpiStatus* st, int code, const char* format, ...) {
    va_list args;
    st->code = code;
    va_start(args, format);
    vsnprintf(st->message, sizeof st->message, format, args);
    va_end(args);
    return 0;
}
static int fire_spi_success(FireSpiStatus* st) { st->code = 0; st->message[0] = 0; return 1; }

/* ---- a list of names ---- */
typedef struct FireSpiList { char** items; size_t count, cap; } FireSpiList;
static void fire_spi_listAdd(FireSpiList* l, const char* s) {
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
static void fire_spi_listFree(FireSpiList* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}

#endif
