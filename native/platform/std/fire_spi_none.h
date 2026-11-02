/* fire native platform layer, SPI part in C for a platform without SPI (Windows, macOS, a board without a package of its own, a Linux without the spidev
 * headers): the list of devices is empty and every call fails with "not supported". The simulated device `sim` of the bridge works everywhere. A platform package
 * offers `fire_spi.h` (the generated file defines FIRE_PLATFORM_SPI_C_HEADER for it) with the functions listed in fire_spi_c.h. */
#ifndef FIRE_SPI_NONE_H
#define FIRE_SPI_NONE_H

#include "fire_spi_c.h"

struct FireSpiDevice { int unused; };

static int fire_spi_supported(void) { return 0; }
static int fire_spi_none(FireSpiStatus* st) { return fire_spi_fail(st, FIRE_SPI_E_UNSUPPORTED, "This platform has no SPI (the simulated device \"sim\" works everywhere)."); }

static int fire_spi_devices(FireSpiList* names, FireSpiStatus* st) { fire_spi_listFree(names); return fire_spi_success(st); }
static FireSpiDevice* fire_spi_openDevice(const char* name, int mode, int speedHz, int lsbFirst, FireSpiStatus* st) {
    (void)name; (void)mode; (void)speedHz; (void)lsbFirst;
    fire_spi_fail(st, FIRE_SPI_E_NOT_FOUND, "There is no such SPI device on this platform.");
    return NULL;
}
static int fire_spi_configure(FireSpiDevice* d, int mode, int speedHz, int lsbFirst, FireSpiStatus* st) { (void)d; (void)mode; (void)speedHz; (void)lsbFirst; return fire_spi_none(st); }
static int fire_spi_transfer(FireSpiDevice* d, const uint8_t* out, uint8_t* in, int n, FireSpiStatus* st) { (void)d; (void)out; (void)in; (void)n; return fire_spi_none(st); }
static void fire_spi_closeDevice(FireSpiDevice* d) { (void)d; }

#endif
