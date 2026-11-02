/* fire native platform layer, serial ports for the devices bridge on a platform without any: there are no `serial` devices (the loopback device still works).
 * The interface is described in fire_dev_c.h. */
#ifndef FIRE_DEV_NONE_H
#define FIRE_DEV_NONE_H

#include "fire_dev_c.h"

typedef struct FireDevSerialPort { int unused; } FireDevSerialPort;

static void fire_dev_serialNames(FireDevNames* names) { (void)names; }
static void fire_dev_serialInit(FireDevSerialPort* p) { p->unused = 0; }
static int fire_dev_serialOpen(FireDevSerialPort* p, const char* name) { (void)p; (void)name; return 0; }
static void fire_dev_serialClose(FireDevSerialPort* p) { (void)p; }
static int64_t fire_dev_serialRead(FireDevSerialPort* p, uint8_t* buffer, size_t n) { (void)p; (void)buffer; (void)n; return -1; }
static int fire_dev_serialWrite(FireDevSerialPort* p, const uint8_t* data, size_t n) { (void)p; (void)data; (void)n; return 0; }

#endif
