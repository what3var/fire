/* fire devices package: serial ports for the devices bridge on the ESP32: the UART driver of ESP-IDF, 115200 baud 8N1, reading without waiting.
 * The ports are the UARTs listed in FIRE_SERIAL_PORTS (a string like "1,2": the numbers of the UARTs; default "1"; UART 0 is the console and not listed). Pins:
 * the defaults of the chip (FIRE_UART<n>_TX / FIRE_UART<n>_RX can set them, e.g. FIRE_UART1_TX=17). (Not tried on a board yet.) The interface is described
 * in fire_dev_c.h. */
#ifndef FIRE_DEV_ESP32_H
#define FIRE_DEV_ESP32_H

#include "fire_dev_c.h"
#include "driver/uart.h"

#ifndef FIRE_SERIAL_PORTS
#define FIRE_SERIAL_PORTS "1"
#endif

typedef struct FireDevSerialPort { int port; } FireDevSerialPort;

static void fire_dev_serialNames(FireDevNames* names) {
    const char* list = FIRE_SERIAL_PORTS;
    size_t length = strlen(list), i = 0;
    while (i <= length) {
        const char* comma = strchr(list + i, ',');
        size_t next = comma ? (size_t)(comma - list) : length;
        if (next > i) {
            char name[40];
            snprintf(name, sizeof name, "UART%.*s", (int)(next - i), list + i);
            fire_dev_namesAdd(names, name);
        }
        i = next + 1;
    }
}

static int fire_dev_espPin(int port, int tx) {
    (void)tx;
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

static void fire_dev_serialInit(FireDevSerialPort* p) { p->port = -1; }
static void fire_dev_serialClose(FireDevSerialPort* p) {
    if (p->port >= 0) { uart_driver_delete((uart_port_t)p->port); p->port = -1; }
}
static int fire_dev_serialOpen(FireDevSerialPort* p, const char* name) {
    uart_config_t config;
    int port;
    fire_dev_serialClose(p);
    if (strlen(name) < 5) return 0;
    port = name[4] - '0';
    if (port < 1 || port >= UART_NUM_MAX) return 0;
    memset(&config, 0, sizeof config);
    config.baud_rate = 115200;
    config.data_bits = UART_DATA_8_BITS;
    config.parity = UART_PARITY_DISABLE;
    config.stop_bits = UART_STOP_BITS_1;
    config.flow_ctrl = UART_HW_FLOWCTRL_DISABLE;
    if (uart_param_config((uart_port_t)port, &config) != ESP_OK) return 0;
    if (uart_set_pin((uart_port_t)port, fire_dev_espPin(port, 1), fire_dev_espPin(port, 0), UART_PIN_NO_CHANGE, UART_PIN_NO_CHANGE) != ESP_OK) return 0;
    if (uart_driver_install((uart_port_t)port, 1024, 1024, 0, NULL, 0) != ESP_OK) return 0;
    p->port = port;
    return 1;
}
static int64_t fire_dev_serialRead(FireDevSerialPort* p, uint8_t* buffer, size_t n) {
    int r;
    if (p->port < 0) return -1;
    r = uart_read_bytes((uart_port_t)p->port, buffer, (uint32_t)n, 0);
    return r < 0 ? -1 : r;
}
static int fire_dev_serialWrite(FireDevSerialPort* p, const uint8_t* data, size_t n) {
    if (p->port < 0) return 0;
    return uart_write_bytes((uart_port_t)p->port, (const char*)data, n) == (int)n;
}

#endif
