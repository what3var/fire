/* fire native platform layer, I2C part in C on the ESP32: the master driver of ESP-IDF (driver/i2c_master.h, IDF 5.2 and newer). The buses are called "i2c-0" and
 * "i2c-1" (the controllers of the chip; the ESP32-C3 has only "i2c-0"). The pins are the defaults of the board definition below; set FIRE_I2C0_SDA / FIRE_I2C0_SCL
 * (and FIRE_I2C1_...) in the target's defines to use others. The internal pull-ups are on (weak: for short wires and a few devices; real boards have external
 * ones). The driver wants a device handle per address: they are made when an address is first used and kept until the speed changes or the bus is closed.
 * (Not tried on a board yet.) The interface is the one of fire_i2c_c.h. */
#ifndef FIRE_I2C_ESP32_H
#define FIRE_I2C_ESP32_H

#include <driver/i2c_master.h>
#include <esp_err.h>

#include "fire_i2c_c.h"

#ifndef FIRE_I2C0_SDA
#define FIRE_I2C0_SDA 21
#endif
#ifndef FIRE_I2C0_SCL
#define FIRE_I2C0_SCL 22
#endif
#ifndef FIRE_I2C1_SDA
#define FIRE_I2C1_SDA 18
#endif
#ifndef FIRE_I2C1_SCL
#define FIRE_I2C1_SCL 19
#endif
#ifndef FIRE_I2C_TIMEOUT_MS
#define FIRE_I2C_TIMEOUT_MS 200
#endif

/* "0x48" */
static void fire_i2c_hex(char* text, size_t size, int address) { snprintf(text, size, "0x%02X", address & 0x7F); }

typedef struct FireI2cDevice {
    int address;
    i2c_master_dev_handle_t handle;
} FireI2cDevice;
struct FireI2cBus {
    int port;
    int speed;
    i2c_master_bus_handle_t handle;
    FireI2cDevice* devices;
    size_t deviceCount, deviceCap;
};

static int fire_i2c_supported(void) { return 1; }

static int fire_i2c_buses(FireI2cList* names, FireI2cStatus* st) {
    int p;
    fire_i2c_listFree(names);
    for (p = 0; p < (int)SOC_I2C_NUM; p++) {
        char name[16];
        snprintf(name, sizeof name, "i2c-%d", p);
        fire_i2c_listAdd(names, name);
    }
    return fire_i2c_success(st);
}

static int fire_i2c_failEsp(FireI2cStatus* st, esp_err_t err, const char* what, int address) {
    char hex[8];
    fire_i2c_hex(hex, sizeof hex, address);
    if (err == ESP_ERR_TIMEOUT) return fire_i2c_fail(st, FIRE_I2C_E_TIMEOUT, "%s (address %s) timed out.", what, hex);
    if (err == ESP_ERR_NOT_FOUND || err == ESP_FAIL || err == ESP_ERR_INVALID_STATE) return fire_i2c_fail(st, FIRE_I2C_E_NO_ACK, "No device answered at address %s.", hex);
    if (err == ESP_ERR_INVALID_ARG) return fire_i2c_fail(st, FIRE_I2C_E_INVALID_ARGUMENT, "%s: invalid argument.", what);
    if (err == ESP_ERR_NO_MEM) return fire_i2c_fail(st, FIRE_I2C_E_OTHER, "%s: out of memory.", what);
    return fire_i2c_fail(st, FIRE_I2C_E_OTHER, "%s: %s", what, esp_err_to_name(err));
}

static FireI2cBus* fire_i2c_openBus(const char* name, int speedHz, FireI2cStatus* st) {
    size_t n = strlen(name), i;
    int valid = n > 4 && strncmp(name, "i2c-", 4) == 0;
    int port;
    i2c_master_bus_config_t cfg;
    i2c_master_bus_handle_t handle = NULL;
    esp_err_t err;
    FireI2cBus* b;
    for (i = 4; valid && i < n; i++) if (name[i] < '0' || name[i] > '9') valid = 0;
    port = valid && n < 7 ? atoi(name + 4) : -1;
    if (port < 0 || port >= (int)SOC_I2C_NUM) { fire_i2c_fail(st, FIRE_I2C_E_NOT_FOUND, "There is no I2C bus '%s' on this chip.", name); return NULL; }
    memset(&cfg, 0, sizeof cfg);
    cfg.i2c_port = (i2c_port_num_t)port;
    cfg.sda_io_num = (gpio_num_t)(port == 0 ? FIRE_I2C0_SDA : FIRE_I2C1_SDA);
    cfg.scl_io_num = (gpio_num_t)(port == 0 ? FIRE_I2C0_SCL : FIRE_I2C1_SCL);
    cfg.clk_source = I2C_CLK_SRC_DEFAULT;
    cfg.glitch_ignore_cnt = 7;
    cfg.flags.enable_internal_pullup = 1;
    err = i2c_new_master_bus(&cfg, &handle);
    if (err == ESP_ERR_NOT_FOUND || err == ESP_ERR_INVALID_STATE) { fire_i2c_fail(st, FIRE_I2C_E_BUSY, "The I2C bus %s is already in use.", name); return NULL; }
    if (err != ESP_OK) { fire_i2c_fail(st, FIRE_I2C_E_OTHER, "i2c_new_master_bus: %s", esp_err_to_name(err)); return NULL; }
    b = (FireI2cBus*)calloc(1, sizeof(FireI2cBus));
    if (b == NULL) { i2c_del_master_bus(handle); fire_i2c_fail(st, FIRE_I2C_E_OTHER, "Out of memory."); return NULL; }
    b->port = port;
    b->speed = speedHz > 0 ? speedHz : 100000;
    b->handle = handle;
    fire_i2c_success(st);
    return b;
}

static void fire_i2c_dropDevices(FireI2cBus* b) {
    size_t i;
    for (i = 0; i < b->deviceCount; i++) i2c_master_bus_rm_device(b->devices[i].handle);
    b->deviceCount = 0;
}

static int fire_i2c_setSpeed(FireI2cBus* b, int speedHz, FireI2cStatus* st) {
    if (speedHz < 1000 || speedHz > 1000000) return fire_i2c_fail(st, FIRE_I2C_E_INVALID_ARGUMENT, "The speed must be between 1 kHz and 1 MHz.");
    if (speedHz != b->speed) { fire_i2c_dropDevices(b); b->speed = speedHz; }   /* (the handles carry the speed: they are made again when used) */
    return fire_i2c_success(st);
}

static int fire_i2c_deviceFor(FireI2cBus* b, int address, i2c_master_dev_handle_t* handle, FireI2cStatus* st) {
    i2c_device_config_t cfg;
    esp_err_t err;
    size_t i;
    for (i = 0; i < b->deviceCount; i++) if (b->devices[i].address == address) { *handle = b->devices[i].handle; return fire_i2c_success(st); }
    if (b->deviceCount == b->deviceCap) {
        size_t cap = b->deviceCap ? b->deviceCap * 2 : 4;
        FireI2cDevice* grown = (FireI2cDevice*)realloc(b->devices, sizeof(FireI2cDevice) * cap);
        if (grown == NULL) return fire_i2c_fail(st, FIRE_I2C_E_OTHER, "add device: out of memory.");
        b->devices = grown;
        b->deviceCap = cap;
    }
    memset(&cfg, 0, sizeof cfg);
    cfg.dev_addr_length = I2C_ADDR_BIT_LEN_7;
    cfg.device_address = (uint16_t)address;
    cfg.scl_speed_hz = (uint32_t)b->speed;
    err = i2c_master_bus_add_device(b->handle, &cfg, handle);
    if (err != ESP_OK) return fire_i2c_failEsp(st, err, "add device", address);
    b->devices[b->deviceCount].address = address;
    b->devices[b->deviceCount].handle = *handle;
    b->deviceCount++;
    return fire_i2c_success(st);
}

static int fire_i2c_write(FireI2cBus* b, int address, const uint8_t* bytes, int n, FireI2cStatus* st) {
    i2c_master_dev_handle_t d;
    esp_err_t err;
    if (!fire_i2c_deviceFor(b, address, &d, st)) return 0;
    err = i2c_master_transmit(d, bytes, (size_t)n, FIRE_I2C_TIMEOUT_MS);
    return err == ESP_OK ? fire_i2c_success(st) : fire_i2c_failEsp(st, err, "I2C write", address);
}

static int fire_i2c_read(FireI2cBus* b, int address, uint8_t* bytes, int n, FireI2cStatus* st) {
    i2c_master_dev_handle_t d;
    esp_err_t err;
    if (!fire_i2c_deviceFor(b, address, &d, st)) return 0;
    err = i2c_master_receive(d, bytes, (size_t)n, FIRE_I2C_TIMEOUT_MS);
    return err == ESP_OK ? fire_i2c_success(st) : fire_i2c_failEsp(st, err, "I2C read", address);
}

static int fire_i2c_writeRead(FireI2cBus* b, int address, const uint8_t* out, int nout, uint8_t* in, int nin, FireI2cStatus* st) {
    i2c_master_dev_handle_t d;
    esp_err_t err;
    if (!fire_i2c_deviceFor(b, address, &d, st)) return 0;
    err = i2c_master_transmit_receive(d, out, (size_t)nout, in, (size_t)nin, FIRE_I2C_TIMEOUT_MS);
    return err == ESP_OK ? fire_i2c_success(st) : fire_i2c_failEsp(st, err, "I2C transfer", address);
}

static int fire_i2c_probe(FireI2cBus* b, int address, int* present, FireI2cStatus* st) {
    esp_err_t err = i2c_master_probe(b->handle, (uint16_t)address, FIRE_I2C_TIMEOUT_MS);
    *present = err == ESP_OK;
    if (err == ESP_OK || err == ESP_ERR_NOT_FOUND) return fire_i2c_success(st);
    if (err == ESP_ERR_TIMEOUT) return fire_i2c_fail(st, FIRE_I2C_E_TIMEOUT, "I2C probe timed out.");
    return fire_i2c_success(st);   /* (a device that does not answer: not there) */
}

static void fire_i2c_closeBus(FireI2cBus* b) {
    if (!b) return;
    fire_i2c_dropDevices(b);
    free(b->devices);
    if (b->handle) i2c_del_master_bus(b->handle);
    free(b);
}

#endif
