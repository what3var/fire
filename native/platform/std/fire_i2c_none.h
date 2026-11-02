/* fire native platform layer, I2C part in C for a platform without I2C buses (Windows, macOS, a board without a package of its own, a Linux without the i2c-dev
 * headers): the list of buses is empty and every call fails with "not supported". The simulated bus `sim` of the bridge works everywhere. A platform package
 * offers `fire_i2c.h` (the generated file defines FIRE_PLATFORM_I2C_C_HEADER for it) with the functions listed in fire_i2c_c.h. */
#ifndef FIRE_I2C_NONE_H
#define FIRE_I2C_NONE_H

#include "fire_i2c_c.h"

struct FireI2cBus { int unused; };

static int fire_i2c_supported(void) { return 0; }
static int fire_i2c_none(FireI2cStatus* st) { return fire_i2c_fail(st, FIRE_I2C_E_UNSUPPORTED, "This platform has no I2C buses (the simulated bus \"sim\" works everywhere)."); }

static int fire_i2c_buses(FireI2cList* names, FireI2cStatus* st) { fire_i2c_listFree(names); return fire_i2c_success(st); }
static FireI2cBus* fire_i2c_openBus(const char* name, int speedHz, FireI2cStatus* st) {
    (void)name; (void)speedHz;
    fire_i2c_fail(st, FIRE_I2C_E_NOT_FOUND, "There is no such I2C bus on this platform.");
    return NULL;
}
static int fire_i2c_setSpeed(FireI2cBus* b, int speedHz, FireI2cStatus* st) { (void)b; (void)speedHz; return fire_i2c_none(st); }
static int fire_i2c_write(FireI2cBus* b, int address, const uint8_t* bytes, int n, FireI2cStatus* st) { (void)b; (void)address; (void)bytes; (void)n; return fire_i2c_none(st); }
static int fire_i2c_read(FireI2cBus* b, int address, uint8_t* bytes, int n, FireI2cStatus* st) { (void)b; (void)address; (void)bytes; (void)n; return fire_i2c_none(st); }
static int fire_i2c_writeRead(FireI2cBus* b, int address, const uint8_t* out, int nout, uint8_t* in, int nin, FireI2cStatus* st) {
    (void)b; (void)address; (void)out; (void)nout; (void)in; (void)nin; return fire_i2c_none(st);
}
static int fire_i2c_probe(FireI2cBus* b, int address, int* present, FireI2cStatus* st) { (void)b; (void)address; *present = 0; return fire_i2c_none(st); }
static void fire_i2c_closeBus(FireI2cBus* b) { (void)b; }

#endif
