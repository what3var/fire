// fire native platform layer, serial ports for the devices bridge on the ESP32: the UART driver of ESP-IDF, 115200 baud 8N1, reading without waiting.
// The ports are the UARTs listed in FIRE_SERIAL_PORTS (a string like "1,2": the numbers of the UARTs; default "1"; UART 0 is the console and not listed). Pins: the
// defaults of the chip (FIRE_UART<n>_TX / FIRE_UART<n>_RX can set them, e.g. FIRE_UART1_TX=17). (Not tried on a board yet.)
#pragma once

#include <cstdint>
#include <string>
#include <vector>
#include "driver/uart.h"

#ifndef FIRE_SERIAL_PORTS
#define FIRE_SERIAL_PORTS "1"
#endif

namespace fire {
namespace plat {
namespace dev {

inline std::vector<std::string> serialNames() {
    std::vector<std::string> names;
    std::string list = FIRE_SERIAL_PORTS;
    size_t i = 0;
    while (i <= list.size()) {
        size_t next = list.find(',', i);
        if (next == std::string::npos) next = list.size();
        std::string n = list.substr(i, next - i);
        if (!n.empty()) names.push_back("UART" + n);
        i = next + 1;
    }
    return names;
}

class SerialPort {
public:
    ~SerialPort() { close(); }
    bool open(const std::string& name) {
        close();
        if (name.size() < 5) return false;
        int port = name[4] - '0';
        if (port < 1 || port >= UART_NUM_MAX) return false;
        uart_config_t config = {};
        config.baud_rate = 115200;
        config.data_bits = UART_DATA_8_BITS;
        config.parity = UART_PARITY_DISABLE;
        config.stop_bits = UART_STOP_BITS_1;
        config.flow_ctrl = UART_HW_FLOWCTRL_DISABLE;
        if (uart_param_config((uart_port_t)port, &config) != ESP_OK) return false;
        if (uart_set_pin((uart_port_t)port, pin(port, true), pin(port, false), UART_PIN_NO_CHANGE, UART_PIN_NO_CHANGE) != ESP_OK) return false;
        if (uart_driver_install((uart_port_t)port, 1024, 1024, 0, nullptr, 0) != ESP_OK) return false;
        port_ = port;
        return true;
    }
    void close() {
        if (port_ >= 0) { uart_driver_delete((uart_port_t)port_); port_ = -1; }
    }
    int64_t read(uint8_t* buffer, size_t n) {
        if (port_ < 0) return -1;
        int r = uart_read_bytes((uart_port_t)port_, buffer, (uint32_t)n, 0);
        return r < 0 ? -1 : r;
    }
    bool write(const uint8_t* data, size_t n) {
        if (port_ < 0) return false;
        return uart_write_bytes((uart_port_t)port_, reinterpret_cast<const char*>(data), n) == (int)n;
    }
private:
    static int pin(int port, bool tx) {
        switch (port) {
#ifdef FIRE_UART1_TX
            case 1: return tx ? FIRE_UART1_TX : FIRE_UART1_RX;
#endif
#ifdef FIRE_UART2_TX
            case 2: return tx ? FIRE_UART2_TX : FIRE_UART2_RX;
#endif
            default: return UART_PIN_NO_CHANGE;
        }
    }
    int port_ = -1;
};

}  // namespace dev
}  // namespace plat
}  // namespace fire
