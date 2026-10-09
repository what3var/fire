// fire native platform layer, SPI part on the ESP32: the SPI master driver of ESP-IDF (driver/spi_master.h). The devices are called "spi-2" and "spi-3" (the general purpose controllers, SPI2_HOST and SPI3_HOST: "HSPI" and
// "VSPI" of the classic ESP32); "spi-2.5" names the chip select pin (GPIO 5) of the device, without it the default of the board definition below is used - several devices on one bus differ in the chip select. The pins are
// the defaults of the classic ESP32; set FIRE_SPI2_MOSI / _MISO / _SCLK / _CS (and FIRE_SPI3_...) in the target's defines to use others. The bus of a host is set up when its first device is opened and freed when the last is
// closed. Transfers go through a DMA-capable piece buffer, polled (the call returns when the transfer is done). (Not tried on a board yet.) The interface is the one of fire_spi_none.hpp.
#pragma once

#include <driver/spi_master.h>
#include <esp_heap_caps.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

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

namespace fire {
namespace plat {
namespace spi {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_Timeout = 8 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

constexpr int kPiece = 2048;

struct Device {
    int host = 2;
    int cs = 0;
    int mode = 0;
    int speed = 1000000;
    bool lsb = false;
    spi_device_handle_t handle = nullptr;
    uint8_t* dma = nullptr;   // kPiece bytes out, then kPiece bytes in
};

inline int& users(int host) {
    static int counts[4] = {0, 0, 0, 0};
    return counts[host];
}

inline bool supported() { return true; }

inline Status devices(std::vector<std::string>& names) {
    names.clear();
    names.push_back("spi-2");
#if defined(SPI3_HOST)
    names.push_back("spi-3");
#endif
    return success();
}

inline Status addDevice(Device* d) {
    spi_device_interface_config_t cfg;
    std::memset(&cfg, 0, sizeof cfg);
    cfg.mode = (uint8_t)(d->mode & 3);
    cfg.clock_speed_hz = d->speed;
    cfg.spics_io_num = d->cs;
    cfg.queue_size = 1;
    cfg.flags = d->lsb ? (SPI_DEVICE_TXBIT_LSBFIRST | SPI_DEVICE_RXBIT_LSBFIRST) : 0;
    esp_err_t err = spi_bus_add_device((spi_host_device_t)(d->host == 2 ? SPI2_HOST : SPI3_HOST), &cfg, &d->handle);
    if (err != ESP_OK) return fail(err == ESP_ERR_NOT_FOUND ? E_Busy : E_Other, std::string("spi_bus_add_device: ") + esp_err_to_name(err));
    return success();
}

inline Status openDevice(const std::string& name, int mode, int speedHz, bool lsbFirst, Device*& out) {
    out = nullptr;
    // "spi-2" or "spi-2.5"
    bool valid = name.size() >= 5 && name.compare(0, 4, "spi-") == 0 && (name[4] == '2' || name[4] == '3');
    int host = valid ? name[4] - '0' : 0;
    int cs = host == 2 ? FIRE_SPI2_CS : FIRE_SPI3_CS;
    if (valid && name.size() > 5) {
        valid = name[5] == '.' && name.size() > 6 && name.size() < 9;
        for (size_t i = 6; valid && i < name.size(); i++) if (name[i] < '0' || name[i] > '9') valid = false;
        if (valid) cs = std::atoi(name.c_str() + 6);
    }
#if !defined(SPI3_HOST)
    if (host == 3) valid = false;
#endif
    if (!valid) return fail(E_NotFound, "There is no SPI device '" + name + "' (the devices are called \"spi-2\", \"spi-3\"; \"spi-2.5\" names the chip select pin).");
    if (users(host) == 0) {
        spi_bus_config_t bus;
        std::memset(&bus, 0, sizeof bus);
        bus.mosi_io_num = host == 2 ? FIRE_SPI2_MOSI : FIRE_SPI3_MOSI;
        bus.miso_io_num = host == 2 ? FIRE_SPI2_MISO : FIRE_SPI3_MISO;
        bus.sclk_io_num = host == 2 ? FIRE_SPI2_SCLK : FIRE_SPI3_SCLK;
        bus.quadwp_io_num = -1;
        bus.quadhd_io_num = -1;
        bus.max_transfer_sz = kPiece;
        esp_err_t err = spi_bus_initialize((spi_host_device_t)(host == 2 ? SPI2_HOST : SPI3_HOST), &bus, SPI_DMA_CH_AUTO);
        if (err == ESP_ERR_INVALID_STATE) return fail(E_Busy, "The SPI bus " + name + " is already in use.");
        if (err != ESP_OK) return fail(E_Other, std::string("spi_bus_initialize: ") + esp_err_to_name(err));
    }
    Device* d = new Device();
    d->host = host;
    d->cs = cs;
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    d->dma = static_cast<uint8_t*>(heap_caps_malloc((size_t)kPiece * 2, MALLOC_CAP_DMA));
    if (!d->dma) { delete d; return fail(E_Other, "Out of memory for the SPI buffers."); }
    Status st = addDevice(d);
    if (!st.ok()) {
        heap_caps_free(d->dma);
        delete d;
        if (users(host) == 0) spi_bus_free((spi_host_device_t)(host == 2 ? SPI2_HOST : SPI3_HOST));
        return st;
    }
    users(host)++;
    out = d;
    return success();
}

inline Status configure(Device* d, int mode, int speedHz, bool lsbFirst) {
    if (speedHz < 1 || speedHz > 80000000) return fail(E_InvalidArgument, "The speed must be between 1 Hz and 80 MHz.");
    spi_bus_remove_device(d->handle);
    d->handle = nullptr;
    d->mode = mode;
    d->speed = speedHz;
    d->lsb = lsbFirst;
    return addDevice(d);
}

inline Status transfer(Device* d, const uint8_t* out, uint8_t* in, int n) {
    if (!d->handle) return fail(E_InvalidHandle, "The SPI device is not set up.");
    for (int done = 0; done < n; done += kPiece) {
        int piece = std::min(kPiece, n - done);
        if (out) std::memcpy(d->dma, out + done, (size_t)piece);
        else std::memset(d->dma, 0, (size_t)piece);
        spi_transaction_t t;
        std::memset(&t, 0, sizeof t);
        t.length = (size_t)piece * 8;
        t.tx_buffer = d->dma;
        t.rx_buffer = d->dma + kPiece;
        esp_err_t err = spi_device_polling_transmit(d->handle, &t);
        if (err == ESP_ERR_TIMEOUT) return fail(E_Timeout, "SPI transfer timed out.");
        if (err != ESP_OK) return fail(E_Other, std::string("SPI transfer: ") + esp_err_to_name(err));
        if (in) std::memcpy(in + done, d->dma + kPiece, (size_t)piece);
    }
    return success();
}

inline void closeDevice(Device* d) {
    if (!d) return;
    if (d->handle) spi_bus_remove_device(d->handle);
    if (d->dma) heap_caps_free(d->dma);
    if (--users(d->host) == 0) spi_bus_free((spi_host_device_t)(d->host == 2 ? SPI2_HOST : SPI3_HOST));
    delete d;
}

}  // namespace spi
}  // namespace plat
}  // namespace fire
