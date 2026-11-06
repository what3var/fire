# ESP32

`-t esp32` builds the program as a component of an [ESP-IDF](https://docs.espressif.com/projects/esp-idf/) project; spark builds, flashes, runs and
debugs it on the board over its serial port.

## Setup

1. Install ESP-IDF (5.x; tested with 5.5) and note its folder (the one with `export.sh` / `export.bat`).
2. In spark: *Tools > Toolchain Settings > ESP32 Board* - the ESP-IDF folder (empty: `IDF_PATH`).
3. Plug in the board. The serial port and the baud rate are in *Project > Properties > Native & Toolchain > Board (ESP32)*; *auto* finds the only USB
   serial port.

## First program

```fire
#import "time"

for (var i = 0; i < 5; i++) {
    print("Hello from the ESP32 " + i)
    Sleep(500ms)
}
```

In spark: choose the target **esp32** in the *Build* tool strip, then `F5`. spark writes an ESP-IDF project, runs `idf.py build flash` and shows the
board's console in the **Output** panel (the line below it is the board's input). In *Debug* mode breakpoints and stepping work over the serial line.

From the command line:

```bash
forge build hello.fire -t esp32 -o hello/main
```

writes the component `main` (the C files and a `CMakeLists.txt`). Next to it, `hello/CMakeLists.txt`:

```cmake
cmake_minimum_required(VERSION 3.16)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(hello)
```

then `idf.py build flash monitor` in `hello/`.

## Specifics

- **`float` is 32 bits** (the FPU of the ESP32 is single precision); `int` stays 64 bits unless `#intwidth 32`.
- **Libraries:** `print`, `io`, `filesystem`, `devices`, `time`, `reflection`, `linq` and the packages (`net`, `http`, `tls`, `wifi`, `gpio`, `i2c`,
  `spi`, `audio`, `json`, ...). `graphics`, `windows` and `ui` are not available on the target.
- **Threads** are FreeRTOS tasks (stack 8 KB, `stackBytes` of the target); the program runs on a task of its own with `FIRE_MAIN_STACK_BYTES`
  (16 KB) because the main task of ESP-IDF has only 3.5 KB.
- **WiFi first, then the network:** [`WiFi.Station`](../networking/wifi.md) joins, then `net`, `http` and `tls` work (lwIP, mbedTLS).
- **Pins:** GPIO through the driver of ESP-IDF; I2C bus 0 on GPIO 21/22, bus 1 on 18/19; SPI hosts 2 and 3; sound as PWM on a pin
  (`new Audio.Output(25)`). The pins can be changed with defines of the target ([GPIO](../devices/gpio.md), [I2C](../devices/i2c.md),
  [SPI](../devices/spi.md), [audio](../audio/README.md)). Add the components the packages name (`esp_wifi`, `esp_driver_i2c`, ...).
- **Tested** in Espressif's QEMU emulator (output like the VM); the drivers of WiFi, GPIO, I2C, SPI and PWM are written against the IDF API and
  compiled, not yet tried on a real board.
