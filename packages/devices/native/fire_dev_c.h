/* fire devices package: serial ports for the devices bridge in C: what the implementations share - a list of port names.
 *
 * The devices package brings, one file per platform (platformSources of devices.fireproj),:
 *   void    fire_dev_serialNames(FireDevNames*)          the serial ports of the machine (like SerialPort.GetPortNames)
 *   FireDevSerialPort                                    a port (115200 baud, 8N1); fire_dev_serialInit makes a closed one
 *   void    fire_dev_serialInit(FireDevSerialPort*)
 *   int     fire_dev_serialOpen(FireDevSerialPort*, const char* name)       1: open
 *   void    fire_dev_serialClose(FireDevSerialPort*)
 *   int64_t fire_dev_serialRead(FireDevSerialPort*, uint8_t*, size_t)      >0 bytes, 0 nothing yet, -1 port gone
 *   int     fire_dev_serialWrite(FireDevSerialPort*, const uint8_t*, size_t)   1: written */
#ifndef FIRE_DEV_C_H
#define FIRE_DEV_C_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- a list of names (malloc'd texts) ---- */
typedef struct FireDevNames { char** items; size_t count, cap; } FireDevNames;
/* Adds a copy of `n` bytes at `s`: 0 when out of memory. */
static int fire_dev_namesAddN(FireDevNames* l, const char* s, size_t n) {
    char* copy;
    if (l->count == l->cap) {
        size_t cap = l->cap ? l->cap * 2 : 8;
        char** grown = (char**)realloc(l->items, sizeof(char*) * cap);
        if (grown == NULL) return 0;
        l->items = grown;
        l->cap = cap;
    }
    copy = (char*)malloc(n + 1);
    if (copy == NULL) return 0;
    memcpy(copy, s, n);
    copy[n] = 0;
    l->items[l->count++] = copy;
    return 1;
}
static int fire_dev_namesAdd(FireDevNames* l, const char* s) { return fire_dev_namesAddN(l, s, strlen(s)); }
static void fire_dev_namesFree(FireDevNames* l) {
    size_t i;
    for (i = 0; i < l->count; i++) free(l->items[i]);
    free(l->items);
    l->items = NULL;
    l->count = l->cap = 0;
}
static int fire_dev_namesCompare(const void* a, const void* b) { return strcmp(*(char* const*)a, *(char* const*)b); }
/* Sorts the names (by their bytes). */
static void fire_dev_namesSort(FireDevNames* l) { if (l->count > 1) qsort(l->items, l->count, sizeof(char*), fire_dev_namesCompare); }

#endif
