# FreeRTOS (any board)

`-t freertos` writes the program as C files for any board with FreeRTOS. fire does not build them: your board's project (its toolchain, linker
script, startup code) compiles them with the rest.

## First program

```fire
print("Hello from FreeRTOS")
```

```bash
forge build hello.fire -t freertos -o fire
```

writes `fire/` with `fire_program.c`, the runtime headers and the platform layer `platform/freertos/`. Add the folder to the board's build (include
path and the C file), then call the entry point from a task:

```c
extern void fire_start(void);

static void fireTask(void* arg) {
    fire_start();               /* runs the fire program; returns when it ends */
    vTaskDelete(NULL);
}

/* in main(), before vTaskStartScheduler(): */
xTaskCreate(fireTask, "fire", 4096, NULL, tskIDLE_PRIORITY + 1, NULL);
```

## Specifics

- `float` is 32 bits; fire threads are tasks (8 KB stack, `stackBytes`); the headers are `FreeRTOS.h`, `task.h`, `semphr.h` - a port with other names
  changes `includes` in a target of its own ([`fire.native.json`](README.md#firenativejson), `"extends": "freertos"`).
- `print` writes to `stdout` of the C library (retarget its `write` to a UART, as most board projects do); without files, network and devices of a board package the libraries say "not
  supported". A board package of your own (`platformPath`) brings them - the ESP32 target is one.
- A debug build talks to spark framed over stdio.
- **Small boards:** `#intwidth 32` and `#floatwidth 32` on a target with 32-bit pointers make every value 8 bytes instead of 16
  ([types](../language/02-types.md#the-width-of-the-whole-program)).
