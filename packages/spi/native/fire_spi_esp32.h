/* fire spi package: SPI part in C on the ESP32: the SPI master driver of ESP-IDF (driver/spi_master.h). The devices are called "spi-2" and "spi-3" (the
 * general purpose controllers, SPI2_HOST and SPI3_HOST: "HSPI" and "VSPI" of the classic ESP32); "spi-2.5" names the chip select pin (GPIO 5) of the device,
 * without it the default of the board definition below is used - several devices on one bus differ in the chip select. The pins are the defaults of the classic
 * ESP32; set FIRE_SPI2_MOSI / _MISO / _SCLK / _CS (and FIRE_SPI3_...) in the target's defines to use others. The bus of a host is set up when its first device is
 * opened and freed when the last is closed. Transfers go through a DMA-capable piece buffer, polled (the call returns when the transfer is done). (Not tried on a
 * board yet.) The interface is the one of fire_spi_c.h. */
#ifndef FIRE_SPI_ESP32_H
#define FIRE_SPI_ESP32_H

#include <driver/spi_master.h>
#include <esp_err.h>
#include <esp_heap_caps.h>

#include "fire_spi_c.h"

#ifndef FIRE_SPI2_MOSI
#define FIRE_SPI2_MOSI 13
#endif
#ifndef FIRE_SPI2_MISO
#define FIRE_SPI2_MISO 12
#endif
#ifndef FIRE_SPI2_SCLK
#define FIRE_SPI2_SCLK 14
#endif
#ifndef FIRE_SPI2_CS
#define FIRE_SPI2_CS 15
#endif
#ifndef FIRE_SPI3_MOSI
#define FIRE_SPI3_MOSI 23
#endif
#ifndef FIRE_SPI3_MISO
#define FIRE_SPI3_MISO 19
#endif
#ifndef FIRE_SPI3_SCLK
#define FIRE_SPI3_SCLK 18
#endif
#ifndef FIRE_SPI3_CS
#define FIRE_SPI3_CS 5
#endif

#define FIRE_SPI_PIECE 2048

struct FireSpiDevice {
    int host;
    int cs;
    int mode;
    int speed;
    int lsb;
    spi_device_handle_t handle;
    uint8_t* dma;   /* FIRE_SPI_PIECE bytes out, then FIRE_SPI_PIECE bytes in */
};

/* The devices that are open on each host (the bus is freed with the last). */
static int g_fireSpiUsers[4] = { 0, 0, 0, 0 };

static int fire_spi_supported(void) { return 1; }

static int fire_spi_devices(FireSpiList* names, FireSpiStatus* st) {
    fire_spi_listFree(names);
    fire_spi_listAdd(names, "spi-2");
#if defined(SPI3_HOST)
    fire_spi_listAdd(names, "spi-3");
#endif
    return fire_spi_success(st);
}

static spi_host_device_t fire_spi_hostOf(int host) { return (spi_host_device_t)(host == 2 ? SPI2_HOST : SPI3_HOST); }

static int fire_spi_addDevice(FireSpiDevice* d, FireSpiStatus* st) {
    spi_device_interface_config_t cfg;
    esp_err_t err;
    memset(&cfg, 0, sizeof cfg);
    cfg.mode = (uint8_t)(d->mode & 3);
    cfg.clock_speed_hz = d->speed;
    cfg.spics_io_num = d->cs;
    cfg.queue_size = 1;
    cfg.flags = d->lsb ? (SPI_DEVICE_TXBIT_LSBFIRST | SPI_DEVICE_RXBIT_LSBFIRST) : 0;
    err = spi_bus_add_device(fire_spi_hostOf(d->host), &cfg, &d->handle);
    if (err != ESP_OK) return fire_spi_fail(st, err == ESP_ERR_NOT_FOUND ? FIRE_SPI_E_BUSY : FIRE_SPI_E_OTHER, "spi_bus_add_device: %s", esp_err_to_name(err));
    return fire_spi_success(st);
}

static FireSpiDevice* fire_spi_openDevice(const char* name, int mode, int speedHz, int lsbFirst, FireSpiStatus* st) {
    /* "spi-2" or "spi-2.5" */
    size_t n = strlen(name), i;
    int valid = n >= 5 && strncmp(name, "spi-", 4) == 0 && (name[4] == '2' || name[4] == '3');
    int host = valid ? name[4] - '0' : 0;
    int cs = host == 2 ? FIRE_SPI2_CS : FIRE_SPI3_CS;
    FireSpiDevice* d;
    if (valid && n > 5) {
        valid = name[5] == '.' && n > 6 && n < 9;
        for (i = 6; valid && i < n; i++) if (name[i] < '0' || name[i] > '9') valid = 0;
        if (valid) cs = atoi(name + 6);
    }
#if !defined(SPI3_HOST)
    if (host == 3) valid = 0;
#endif
    if (!valid) {
        fire_spi_fail(st, FIRE_SPI_E_NOT_FOUND, "There is no SPI device '%s' (the devices are called \"spi-2\", \"spi-3\"; \"spi-2.5\" names the chip select pin).", name);
        return NULL;
    }
    if (g_fireSpiUsers[host] == 0) {
        spi_bus_config_t bus;
        esp_err_t err;
        memset(&bus, 0, sizeof bus);
        bus.mosi_io_num = host == 2 ? FIRE_SPI2_MOSI : FIRE_SPI3_MOSI;
        bus.miso_io_num = host == 2 ? FIRE_SPI2_MISO : FIRE_SPI3_MISO;
        bus.sclk_io_num = host == 2 ? FIRE_SPI2_SCLK : FIRE_SPI3_SCLK;
        bus.quadwp_io_num = -1;
        bus.quadhd_io_num = -1;
        bus.max_transfer_sz = FIRE_SPI_PIECE;
        err = spi_bus_initialize(fire_spi_hostOf(host), &bus, SPI_DMA_CH_AUTO);
        if (err == ESP_ERR_INVALID_STATE) { fire_spi_fail(st, FIRE_SPI_E_BUSY, "The SPI bus %s is already in use.", name); return NULL; }
        if (err != ESP_OK) { fire_spi_fail(st, FIRE_SPI_E_OTHER, "spi_bus_initialize: %s", esp_err_to_name(err)); return NULL; }
    }
    d = (FireSpiDevice*)calloc(1, sizeof(FireSpiDevice));
    if (d != NULL) d->dma = (uint8_t*)heap_caps_malloc((size_t)FIRE_SPI_PIECE * 2, MALLOC_CAP_DMA);
    if (d == NULL || d->dma == NULL) {
        free(d);
        if (g_fireSpiUsers[host] == 0) spi_bus_free(fire_spi_hostOf(host));
        fire_spi_fail(st, FIRE_SPI_E_OTHER, "Out of memory for the SPI buffers.");
        return NULL;
    }
    d->host = host;
    d->cs = cs;
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    if (!fire_spi_addDevice(d, st)) {
        heap_caps_free(d->dma);
        free(d);
        if (g_fireSpiUsers[host] == 0) spi_bus_free(fire_spi_hostOf(host));
        return NULL;
    }
    g_fireSpiUsers[host]++;
    return d;
}

static int fire_spi_configure(FireSpiDevice* d, int mode, int speedHz, int lsbFirst, FireSpiStatus* st) {
    if (speedHz < 1 || speedHz > 80000000) return fire_spi_fail(st, FIRE_SPI_E_INVALID_ARGUMENT, "The speed must be between 1 Hz and 80 MHz.");
    spi_bus_remove_device(d->handle);
    d->handle = NULL;
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    return fire_spi_addDevice(d, st);
}

static int fire_spi_transfer(FireSpiDevice* d, const uint8_t* out, uint8_t* in, int n, FireSpiStatus* st) {
    int done;
    if (!d->handle) return fire_spi_fail(st, FIRE_SPI_E_INVALID_HANDLE, "The SPI device is not set up.");
    for (done = 0; done < n; done += FIRE_SPI_PIECE) {
        int piece = n - done < FIRE_SPI_PIECE ? n - done : FIRE_SPI_PIECE;
        spi_transaction_t t;
        esp_err_t err;
        if (out) memcpy(d->dma, out + done, (size_t)piece);
        else memset(d->dma, 0, (size_t)piece);
        memset(&t, 0, sizeof t);
        t.length = (size_t)piece * 8;
        t.tx_buffer = d->dma;
        t.rx_buffer = d->dma + FIRE_SPI_PIECE;
        err = spi_device_polling_transmit(d->handle, &t);
        if (err == ESP_ERR_TIMEOUT) return fire_spi_fail(st, FIRE_SPI_E_TIMEOUT, "SPI transfer timed out.");
        if (err != ESP_OK) return fire_spi_fail(st, FIRE_SPI_E_OTHER, "SPI transfer: %s", esp_err_to_name(err));
        if (in) memcpy(in + done, d->dma + FIRE_SPI_PIECE, (size_t)piece);
    }
    return fire_spi_success(st);
}

static void fire_spi_closeDevice(FireSpiDevice* d) {
    if (!d) return;
    if (d->handle) spi_bus_remove_device(d->handle);
    if (d->dma) heap_caps_free(d->dma);
    if (--g_fireSpiUsers[d->host] == 0) spi_bus_free(fire_spi_hostOf(d->host));
    free(d);
}

#endif
