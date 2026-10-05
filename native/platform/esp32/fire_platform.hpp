// fire native platform package "esp32": FreeRTOS in ESP-IDF. Differences to plain FreeRTOS: the stack of a task is given in bytes, the headers live under
// `freertos/` (the target configuration's `includes`), and a fire thread can be pinned to a core (FIRE_THREAD_CORE). Everything the bridges
// need from the chip (GPIO, I2C, SPI, UART, the file system) is added here later.
#pragma once
#ifndef FIRE_FREERTOS_STACK_BYTES
#define FIRE_FREERTOS_STACK_BYTES 1
#endif
#include "../freertos/fire_platform.hpp"
