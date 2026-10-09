// fire native platform layer, I2C part on the ESP32: the master driver of ESP-IDF (driver/i2c_master.h, IDF 5.2 and newer). The buses are called "i2c-0" and "i2c-1" (the controllers of the chip; the ESP32-C3 has
// only "i2c-0"). The pins are the defaults of the board definition below; set FIRE_I2C0_SDA / FIRE_I2C0_SCL (and FIRE_I2C1_...) in the target's defines to use others. The internal pull-ups are on (weak: for
// short wires and a few devices; real boards have external ones). The driver wants a device handle per address: they are made when an address is first used and kept until the speed changes or the bus is closed.
// (Not tried on a board yet.) The interface is the one of fire_i2c_none.hpp.
#pragma once

#include <driver/i2c_master.h>

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

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

namespace fire {
namespace plat {
namespace i2c {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7, E_NoAck = 8, E_Timeout = 9 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

inline std::string hex(int address) {
    char text[8];
    std::snprintf(text, sizeof text, "0x%02X", address & 0x7F);
    return text;
}

struct Device {
    int address;
    i2c_master_dev_handle_t handle;
};
struct Bus {
    int port = 0;
    int speed = 100000;
    i2c_master_bus_handle_t handle = nullptr;
    std::vector<Device> devices;
};

inline bool supported() { return true; }

inline Status buses(std::vector<std::string>& names) {
    names.clear();
    for (int p = 0; p < (int)SOC_I2C_NUM; p++) names.push_back("i2c-" + std::to_string(p));
    return success();
}

inline Status failEsp(esp_err_t err, const std::string& what, int address) {
    if (err == ESP_ERR_TIMEOUT) return fail(E_Timeout, what + " (address " + hex(address) + ") timed out.");
    if (err == ESP_ERR_NOT_FOUND || err == ESP_FAIL || err == ESP_ERR_INVALID_STATE) return fail(E_NoAck, "No device answered at address " + hex(address) + ".");
    if (err == ESP_ERR_INVALID_ARG) return fail(E_InvalidArgument, what + ": invalid argument.");
    if (err == ESP_ERR_NO_MEM) return fail(E_Other, what + ": out of memory.");
    return fail(E_Other, what + ": " + esp_err_to_name(err));
}

inline Status openBus(const std::string& name, int speedHz, Bus*& out) {
    out = nullptr;
    bool valid = name.size() > 4 && name.compare(0, 4, "i2c-") == 0;
    for (size_t i = 4; valid && i < name.size(); i++) if (name[i] < '0' || name[i] > '9') valid = false;
    int port = valid && name.size() < 7 ? std::atoi(name.c_str() + 4) : -1;
    if (port < 0 || port >= (int)SOC_I2C_NUM) return fail(E_NotFound, "There is no I2C bus '" + name + "' on this chip.");
    i2c_master_bus_config_t cfg;
    std::memset(&cfg, 0, sizeof cfg);
    cfg.i2c_port = (i2c_port_num_t)port;
    cfg.sda_io_num = (gpio_num_t)(port == 0 ? FIRE_I2C0_SDA : FIRE_I2C1_SDA);
    cfg.scl_io_num = (gpio_num_t)(port == 0 ? FIRE_I2C0_SCL : FIRE_I2C1_SCL);
    cfg.clk_source = I2C_CLK_SRC_DEFAULT;
    cfg.glitch_ignore_cnt = 7;
    cfg.flags.enable_internal_pullup = true;
    i2c_master_bus_handle_t handle = nullptr;
    esp_err_t err = i2c_new_master_bus(&cfg, &handle);
    if (err == ESP_ERR_NOT_FOUND || err == ESP_ERR_INVALID_STATE) return fail(E_Busy, "The I2C bus " + name + " is already in use.");
    if (err != ESP_OK) return fail(E_Other, std::string("i2c_new_master_bus: ") + esp_err_to_name(err));
    Bus* b = new Bus();
    b->port = port;
    b->speed = speedHz > 0 ? speedHz : 100000;
    b->handle = handle;
    out = b;
    return success();
}

inline void dropDevices(Bus* b) {
    for (Device& d : b->devices) i2c_master_bus_rm_device(d.handle);
    b->devices.clear();
}

inline Status setSpeed(Bus* b, int speedHz) {
    if (speedHz < 1000 || speedHz > 1000000) return fail(E_InvalidArgument, "The speed must be between 1 kHz and 1 MHz.");
    if (speedHz != b->speed) { dropDevices(b); b->speed = speedHz; }   // (the handles carry the speed: they are made again when used)
    return success();
}

inline Status deviceFor(Bus* b, int address, i2c_master_dev_handle_t& handle) {
    for (Device& d : b->devices) if (d.address == address) { handle = d.handle; return success(); }
    i2c_device_config_t cfg;
    std::memset(&cfg, 0, sizeof cfg);
    cfg.dev_addr_length = I2C_ADDR_BIT_LEN_7;
    cfg.device_address = (uint16_t)address;
    cfg.scl_speed_hz = (uint32_t)b->speed;
    esp_err_t err = i2c_master_bus_add_device(b->handle, &cfg, &handle);
    if (err != ESP_OK) return failEsp(err, "add device", address);
    b->devices.push_back({address, handle});
    return success();
}

inline Status write(Bus* b, int address, const uint8_t* bytes, int n) {
    i2c_master_dev_handle_t d;
    Status st = deviceFor(b, address, d);
    if (!st.ok()) return st;
    esp_err_t err = i2c_master_transmit(d, bytes, (size_t)n, FIRE_I2C_TIMEOUT_MS);
    return err == ESP_OK ? success() : failEsp(err, "I2C write", address);
}

inline Status read(Bus* b, int address, uint8_t* bytes, int n) {
    i2c_master_dev_handle_t d;
    Status st = deviceFor(b, address, d);
    if (!st.ok()) return st;
    esp_err_t err = i2c_master_receive(d, bytes, (size_t)n, FIRE_I2C_TIMEOUT_MS);
    return err == ESP_OK ? success() : failEsp(err, "I2C read", address);
}

inline Status writeRead(Bus* b, int address, const uint8_t* out, int nout, uint8_t* in, int nin) {
    i2c_master_dev_handle_t d;
    Status st = deviceFor(b, address, d);
    if (!st.ok()) return st;
    esp_err_t err = i2c_master_transmit_receive(d, out, (size_t)nout, in, (size_t)nin, FIRE_I2C_TIMEOUT_MS);
    return err == ESP_OK ? success() : failEsp(err, "I2C transfer", address);
}

inline Status probe(Bus* b, int address, bool& present) {
    esp_err_t err = i2c_master_probe(b->handle, (uint16_t)address, FIRE_I2C_TIMEOUT_MS);
    present = err == ESP_OK;
    if (err == ESP_OK || err == ESP_ERR_NOT_FOUND) return success();
    if (err == ESP_ERR_TIMEOUT) return fail(E_Timeout, "I2C probe timed out.");
    return success();   // (a device that does not answer: not there)
}

inline void closeBus(Bus* b) {
    if (!b) return;
    dropDevices(b);
    if (b->handle) i2c_del_master_bus(b->handle);
    delete b;
}

}  // namespace i2c
}  // namespace plat
}  // namespace fire
