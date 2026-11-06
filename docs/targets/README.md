# Targets: where fire programs run

A fire program runs in two ways:

| Engine | What it is | Where |
| --- | --- | --- |
| **VM** | the bytecode in the virtual machine of `forge`/spark (.NET) - quick to start, the debugger of spark | Windows, Linux, macOS (the machine you work on) |
| **native** | the program translated to C and built with a C compiler into a program of its own - no .NET, small, fast | every target below |

Both behave the same: the test suite runs every case in both and compares the output. A program does not need to know which engine runs it; where it
must, `#if vm` / `#if native` choose code ([directives](../language/17-preprocessor.md)).

| Target | `-t` | Toolchain | Entry | Page |
| --- | --- | --- | --- | --- |
| Windows | `windows` | GCC (MinGW, w64devkit - spark downloads it), clang, msvc | `main` | [Windows](windows.md) |
| Linux | `linux` | gcc, clang, TinyCC | `main` | [Linux](linux.md) |
| macOS | `macos` | clang | `main` | [macOS](macos.md) |
| Browser | `wasm` | Emscripten | the page | [Browser (WebAssembly)](wasm.md) |
| ESP32 | `esp32` | ESP-IDF | `app_main` | [ESP32](esp32.md) |
| FreeRTOS (any board) | `freertos` | your board's (fire writes the C files) | `fire_start`, called by the board | [FreeRTOS](freertos.md) |

New targets for small and old machines (DOS, BIOS/UEFI, Win 3.x/9x, generic Cortex-M/RISC-V) are being built - see the [roadmap](../ROADMAP.md#13-kleine-und-alte-ziele).

## The first native program

```fire
print("Hello from a native program")
```

```bash
forge run hello.fire
forge build hello.fire --engine native -o hello
./hello
```

`forge build` without `--engine native` makes a **self-contained VM program**: one file that carries the VM and the bytecode (it needs no .NET on the
machine, but it is large). With `--engine native` it is a native program for this machine; `-t <target>` builds for another target (always native - the
VM exists only for the desktop).

In spark: the **engine** box (VM / Native) and the **target** box of the *Build* tool strip; `F5` builds and runs, also natively, and the debugger
works with native programs too (on the ESP32 over the serial port). *Run > Build* writes the program.

## What a target has

| | Windows | Linux | macOS | Browser | ESP32 | FreeRTOS |
| --- | --- | --- | --- | --- | --- | --- |
| fire threads (`fire`, actors) | yes | yes | yes | **no** (an error when built) | yes (tasks) | yes (tasks) |
| `float` | 64 bit | 64 bit | 64 bit | 64 bit | **32 bit** | **32 bit** |
| files | yes | yes | yes | in memory | if a partition is mounted | if the board has them |
| windows, `graphics`, `ui`, `scene3d` | yes | X11 / Wayland | Cocoa | the page's canvas | not available | not available |
| sound (`audio`) | winmm | PulseAudio / ALSA | Audio Queue | Web Audio | PWM on a pin | - |
| network (`net`, `http`) | yes | yes | yes | no | yes (lwIP, after WiFi) | with a board package |
| TLS | SChannel | OpenSSL | OpenSSL | no | mbedTLS | with a board package |
| GPIO, I2C, SPI | simulated | the kernel's devices + simulated | simulated | no | the drivers of ESP-IDF + simulated | simulated |
| USB | yes | yes | yes | no | no | no |
| `process` | yes | yes | yes | no | no | no |
| `Library.Load`, plugins | yes (MinGW) | yes | yes | no | no | no |

What a target does not have fails clearly: a library the target cannot have is an error when the program is built (`E6001`), a feature its platform
layer lacks throws "not supported" when it is used.

## `fire.native.json`

The native build reads its settings from the nearest `fire.native.json` (in the folder of the source or above; `--config` names one). spark's
*Tools > Toolchain Settings* edits the same file. Lines with `//` and trailing commas are allowed:

```json
{
  "engine": "native",            // what `build` makes: "vm" (default) or "native"
  "target": "my-board",          // the default target; otherwise this machine
  "toolchain": "gcc",            // for targets that name none; otherwise the first compiler found
  "stripping": 3,                // how much is left out that the program does not reach: 0 "none" .. 3 "all" (default)
  "targets": {
    "my-board": {
      "extends": "freertos",     // a built-in target or another of this file; only what differs
      "includes": ["FreeRTOS.h", "task.h", "semphr.h"],
      "defines": ["FIRE_THREAD_PRIORITY=3"],
      "entry": { "name": "board_fire_main", "kind": "function", "externC": true },
      "floatWidth": 32, "intWidth": 32, "stackBytes": 6000, "toolchain": "arm"
    }
  },
  "toolchains": {
    "arm": { "extends": "gcc", "compiler": "/opt/arm/bin/arm-none-eabi-g++", "optimization": "-Os", "args": ["-mcpu=cortex-m4"] }
  }
}
```

- **Target fields:** `extends`, `platform` (the platform layer: `windows`, `posix`, `esp32`, `freertos`, `wasm`), `platformPath` (your own), `includes`,
  `defines`, `entry` (`name`, `kind` = `process`|`function`, `externC`), `compileArgs`, `linkLibs`, `supportsThreads`, `floatWidth`, `intWidth`,
  `stackBytes`, `symbols` (for `#if`), `imports`, `embedded`, `toolchain`, `port`/`baud` (a board's serial port).
- **Toolchains** (`kind`): `gcc`, `clang`, `msvc`, `tcc`, `emscripten`, `files` (writes the sources only; `layout: "idf-component"` adds the files of an
  ESP-IDF component, `buildCommand` runs afterwards), `custom` (`command` with `{cpp}`, `{dir}`, `{out}`, `{args}`, `{libs}`). Fields: `compiler`,
  `cCompiler`, `std`, `cStd`, `optimization` (`-O2`, or `"size"` for the small profile), `args`, `libs`, `includeDirs`, `strip` (default true).
- A **project** can set `engine`, `target`, `toolchain`, `linkage`, `stripping`, `floatWidth`, `intWidth`, `defines` in its settings
  ([projects](../tools/projects.md)); the command line goes before both.

## Modes and size

`-m DEBUG|RELEASE|PERFORMANCE`: a debug build has no optimisation, keeps everything and carries the debug agent (spark connects to it); release is
the default; performance leaves out run-time checks. A native program is stripped of symbols; `"optimization": "size"` makes it smaller still. The
generator leaves out what the program does not reach (`stripping`; names given to reflection as literals and `[Keep]` stay).

`linkage` builds the libraries once instead of with every build: [libraries](../libraries/README.md#binary-libraries-linkage).

## How it works

The generator translates the bytecode into C11 (`fire_program.c`) against the runtime `native/runtime/fire_rt.h`; natives of packages in C go into the
same file, C++ natives into `fire_natives.cpp`. Each target has a platform layer (`native/platform/std/fire_<part>_<system>.h`) for files, threads,
time, network, display, sound and the buses. Details for contributors: [native backend](../spec/NATIVE_BACKEND.md).
