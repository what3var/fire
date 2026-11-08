# Writing the natives of a package (C or C++) and connecting them to the prelude

A [package](README.md) - or a [library project](../libraries/README.md) - can bring **natives**: functions written in C or C++ that fire code calls. This is how a package reaches an operating system API, a sensor, a
protocol stack or a fast algorithm. You write the C++ **once**; the compiler uses it in both engines:

| engine | what happens to your C++ |
|---|---|
| native build (`fire.Compiler build --engine native`, any target: Linux, Windows, macOS, ESP32, FreeRTOS, ...) | the program is generated as C (`fire_program.c`); the text of your source files is put into its natives, a C++ file next to it (`fire_natives.cpp`), and the program calls each of your functions through a small C function that the generator writes there; both are linked into one program |
| virtual machine (`fire.Compiler run`, the editor, a packed standalone program) | the compiler builds your sources into a **shared library** (`.dll`, `.so`, `.dylib`) for the machine, with a generated wrapper that exports a small C interface (`native/abi/fire_pkg_abi.h`); the VM loads it and calls it |

So the C# VM never needs to know your functions: it only knows the C ABI, and the wrapper is generated for you.

## Quick start: a library with natives in C

The shortest way is a library project with a folder `native/` (in spark: *File > New > Project... > Native Library (C)*):

```
Calc/
  Calc.fireproj      "type": "library", "native": { "language": "c" }
  calc.fire          the fire side: wraps the natives in a class
  native/calc.h      the natives: C functions on Value
App/
  App.fireproj       "references": [ { "project": "../Calc/Calc.fireproj" } ]
  main.fire
```

`Calc/Calc.fireproj`

```json
{
  "format": 1,
  "name": "Calc",
  "type": "library",
  "settings": { "version": "1.0.0", "description": "Small arithmetic in C" },
  "native": { "language": "c" }
}
```

`Calc/native/calc.h`: every `static inline Value name(Value a, ...)` in the folder becomes the fire function `__name`

```c
#ifndef NATIVES_CALC_H
#define NATIVES_CALC_H

static inline Value calc_add(Value a, Value b) { return Int(a.i + b.i); }

static inline Value calc_clamp(Value v, Value low, Value high) {
    int64_t x = v.i;
    if (x < low.i) x = low.i;
    if (x > high.i) x = high.i;
    return Int(x);
}

#endif
```

`Calc/calc.fire`: the fire side wraps them

```fire
namespace Calc {
    class Fast {
        static int Add(int a, int b) { return __calc_add(a, b) }
        static int Clamp(int v, int low, int high) { return __calc_clamp(v, low, high) }
    }
}
```

`App/main.fire`

```fire
#import "Calc"

print(Calc.Fast.Add(2, 3))          // 5
print(Calc.Fast.Clamp(150, 0, 100)) // 100
```

```bash
forge run App/App.fireproj
forge build App/App.fireproj --engine native
forge build Calc/Calc.fireproj
```

The first run builds the C into a library for the VM ("Building the native part of the package 'project:Calc' ..."; it needs a C compiler on the
machine). The native build puts it into the program. Packing the library (`forge build Calc/Calc.fireproj`, or *Pack as Package*) gives
`Calc/bin/Calc-1.0.0.fpk` - the C files travel in the package, and every machine that installs it builds them itself.

A function that allocates its result (a string, an array) takes a last parameter `OwnList* list`; arguments are `Value`s (`.i` for an int, `.d` for a
float, `strOf(v)->data` for a string, `bufOf(v)->data` for a byte buffer - check `v.kind == K_Buffer` before you use it: fire may pass an array instead). Several packages share one C file in a
native build: give every function and global a prefix of your own. The rest of this page explains all of it in detail.

## 0. C or C++

A package says in its manifest which language its natives are in: `"native": { "language": "c", ... }` or `"c++"` (the default). **C (C99) is the recommended
choice** and what all standard packages use (the project template *Native Library (C)* starts one; *Native Library (C++)* stays for natives in C++):

* a C package goes straight into the generated program (`fire_program.c`), so a C compiler is enough - TinyCC (`--toolchain tcc`), a board without C++;
* a function is named with `"c": "fire_sensor_Read"` (instead of `"cpp": "sensor::Read"`), `"reset"` names a C function, too; every function in the sources is
  `static` and gets a prefix of its own (several packages share the one file);
* the runtime is the same (`native/runtime/fire_rt.h`: `Value`, `Int()`, `strOf(v)->data`, `bufOf(v)->data`, `allocStr`, ...); the platform layers in C are
  included with `#include FIRE_PLATFORM_<PART>_C_HEADER` (`FS`, `NET`, `TLS`, `DEV`, `GPIO`, `I2C`, `SPI`, `USB`, `WIFI`, `AUDIO`, `DISPLAY`; the interface of each
  is described in `native/platform/std/fire_<part>_c.h`);
* in the VM the library is built from a generated `wrapper.c` (`native/abi/fire_pkg_wrapper.h`): one call at a time, `fatal`/`fireError` return to the wrapper
  with `longjmp`, and the error slots of the bridges (`g_ioError`, `g_deviceError`, `g_gfxError`) are kept per calling thread.

The examples in `packages/*/native/fire_bridge_*.h` (process, time, io, net, tls, gpio, ...) show the pattern. The rest of this document describes the C++ route;
what it says about the prelude, the manifest, the platforms and the VM boundary holds for C as well.

## 1. The three parts

```
sensorkit/
  sensorkit.fire     the prelude: fire code that wraps the natives in classes
  sensorkit.hpp      the natives: C++ functions on Value
  sensorkit.json     the forge file (ember forge sensorkit.json)
```

### The native (`sensorkit.hpp`)

A native takes `Value`s and returns a `Value`. The file is put at the top level of the natives of the program (`fire_natives.cpp`) after the runtime (`fire_rt_cpp.hpp`: the
runtime `fire_rt.h` compiled as C++, and the C++ platform layer), so it includes what it needs itself and puts its functions into `namespace fire`:

```cpp
#include <cstdint>

namespace fire {

// crc8(bytes) -> int
inline Value sk_crc8(Value data) {
    FireBytes b;                                  // a byte buffer or an array of bytes (section 2, "Bytes as arguments")
    if (!bytesArg(data, "Sensorkit.Crc8", &b)) return Undef();
    uint8_t crc = 0;
    for (uint32_t i = 0; i < b.length; i++) {
        crc ^= b.data[i];
        for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x07) : (uint8_t)(crc << 1);
    }
    return Int(crc);
}

// hex(bytes) -> string        (takes the list of the scope: it allocates the string)
inline Value sk_hex(Value data, OwnList* list) {
    FireBytes b;
    if (!bytesArg(data, "Sensorkit.Hex", &b)) return Undef();
    Str* s = allocStr(b.length * 2, list);
    static const char digits[] = "0123456789abcdef";
    for (uint32_t i = 0; i < b.length; i++) {
        strChars(s)[2 * i] = digits[b.data[i] >> 4];
        strChars(s)[2 * i + 1] = digits[b.data[i] & 15];
    }
    return StrV(s);
}

}  // namespace fire
```

### The prelude (`sensorkit.fire`)

Natives are plain functions by the name you give them in the package (`__sk_crc8`). Nobody should call them directly: wrap them in a class that checks the arguments
and gives them a nice name:

```
class SensorKit {
    static int Crc8(any buffer) { return __sk_crc8(buffer) }
    static string Hex(any buffer) { return __sk_hex(buffer) }
}
```

### The description (`sensorkit.json`)

```json
{
  "name": "sensorkit", "version": "1.0.0", "author": "Jane", "description": "Checksums for sensor frames",
  "imports": [
    {
      "name": "sensorkit",
      "prelude": "C:/work/sensorkit/sensorkit.fire",
      "native": {
        "sources": [ "C:/work/sensorkit/sensorkit.hpp" ],
        "functions": [
          { "name": "__sk_crc8", "arguments": 1, "returns": "int", "cpp": "sk_crc8" },
          { "name": "__sk_hex",  "arguments": 1, "returns": "string", "cpp": "sk_hex", "needsList": true, "returnsReference": true }
        ]
      }
    }
  ]
}
```

* `name` is what fire code calls, `cpp` the C++ function, `arguments` how many `Value`s it takes.
* `returns`: what the function gives back, as a type of fire - `int`, `int[64]` (a number that may need more than 32 bits: ticks, file sizes),
  `float`, `bool`, `string`, `byte[]`, `any[]`, a class, `undefined` (nothing); without it (or `any`) the result has no type. The type checker gives a
  call this type (`var n = __sk_crc8(frame)` is an `int`; without `returns` it would be TC019), and a result declared `int`, `float` or `bool` is
  checked where it is used (an ArgumentException when the function returns something else). `undefined` always fits (a failure).
* `needsList: true`: the function gets `OwnList* list` as an extra **last** parameter. Everything you allocate for the result (`allocStr`, `allocArr`, `allocBuf`)
  is allocated in that list: it belongs to the scope of the caller, like any value a fire function creates. `returnsReference: true`: the result is such a value.
* `linkLibraries` (in `native`, optional): system libraries the C++ needs on one platform or target, by the same keys as `platformSources`, e.g. `"linkLibraries": { "windows": [ "ws2_32" ] }` - the build links them (`-lws2_32`, `ws2_32.lib`).
* `exceptions` (in `native`): the exception classes of your prelude that your natives throw, e.g. `"exceptions": [ "SensorException" ]`. The class needs a constructor with one text
  argument; the native throws it with `return fireError("SensorException", "no answer");` (a native build constructs the class by name, the VM gets the class and the text from the library).
* `host: true` (per function): in the virtual machine the host runs the function itself instead of the library. It is for what the C ABI cannot carry or what must not run inside a library:
  a wait that has to be abortable (`Sleep`), or a function that needs the unit of a value. The VM looks the function up by name in its own table (`fire.Runtime.HostNatives`), so this is for the
  standard packages; in a native build the `cpp` function is used as always.
* Then: `ember forge sensorkit.json`, `ember install build\sensorkit-1.0.0.fpk`, and in a script `#import "sensorkit"`.

## 2. The value API

Everything is in `native/runtime/fire_rt.h` (the runtime in C, which compiles as C++ for the natives; `fire_rt_cpp.hpp` adds a few overloads with references). The
parts you need most:

| | |
|---|---|
| make | `Int(i)`, `Float((Real)x)`, `Bool(b)`, `Char(c)`, `Undef()`; strings `Str* s = allocStr(length, list)` then `strChars(s)[i] = ...` and `StrV(s)`; arrays `Arr* a = allocArr(n, list)`, `a->items()[i] = v`, `ArrV(a)` (call `retain(v)` for a string you store); buffers `Buf* b = allocBuf(n, list)`, `b->bytes()`, `BufV(b)` |
| read | `v.kind` (`K_Int`, `K_Float`, `K_Bool`, `K_Char`, `K_String`, `K_Array`, `K_Buffer`, `K_Undefined`), `v.i` (int, bool, char), `v.f` / `toR(v)` (a number as a real), `strOf(v)->length` and `->data` (UTF-16), `arrOf(v)->length` / `->items()`, `bufOf(v)->length` / `->bytes()` |
| errors | `return indexError("Array index", i, length);` throws an `IndexOutOfBoundsException` in a native build; `fatal("text")` ends the program (native build) or reports an error to the VM (library). Check your arguments (`v.kind`) and fail with `fatal`; to let the script catch the error, throw an exception of your prelude: `return fireError("SensorException", "text");` (list the class in `exceptions`). |

`Real` is `double`, or `float` with `#floatwidth 32`. A value of another kind than you expect is your bug to catch: the generated code does not check the kind of the arguments for you.

### Bytes as arguments

Never take `bufOf(v)` of an argument unchecked: it does not look at the kind, and an array, a string or a number there is read as a buffer - the program (or the VM, which
runs the same C) crashes. Two functions of the runtime check a byte argument and give the same `ArgumentException` texts in both engines:

| | |
|---|---|
| `bool bytesArg(Value v, const char* what, FireBytes* out)` | for a native that **reads** the bytes (writes, sends, hashes, compresses, queues them). It takes everything that is bytes: a byte buffer (`out->data` is its own memory), an array of ints from 0 to 255 - a plain one or a typed one (a copy made for the call). `out->data` / `out->length` are the bytes (`const uint8_t*`, `uint32_t`). |
| `Buf* bufferArg(Value v, const char* what)` | for a native that **fills** the buffer it gets (reads into it, receives, decodes into it, changes it in place): only a real buffer (`new byte[n]`); an array is an `ArgumentException` (`<what> expects a byte buffer (new byte[n]), not an array.`) - it would be filled in a copy nobody sees. |
| `bool wordsArg(Value v, const char* what, FireWords* out)` | for a native that takes **machine words** (`word`, SPEC 8.10a): a word buffer (`word a[]`, `new word[n]`: `out->data` is its own memory, `const FireWord*`, writable in place through a cast) or an array of ints and words (each cut to the width; a copy made for the call). `out->length` counts words. A byte buffer is an `ArgumentException` (`<what> expects words (a word buffer or an array of ints), not a byte buffer.`), so is an element that is no int or word. |

Both check that the buffer or array is not destroyed (a `DestroyedException`) and that it is bytes at all (`<what> expects bytes (a byte buffer or an array of ints from 0 to 255), not
String.`, `<what>: element 1 of the array is 256 - a byte is an int from 0 to 255.`). On a failure the exception is thrown - `bytesArg` returns false, `bufferArg` NULL - and the native returns
at once, with any value:

```c
static Value fire_sk_Send(Value h, Value data, Value offset, Value count) {
    FireBytes b;
    if (!bytesArg(data, "Sensorkit.Send", &b)) return Undef();
    if (offset.i < 0 || count.i < 0 || offset.i > b.length || count.i > b.length - offset.i) return Int(-1);   /* (the range: your own check) */
    return Int(sk_send(h.i, b.data + offset.i, (size_t)count.i));
}
```

`what` names the function in the message (the name the script knows, e.g. `"IO.Stream.Write"`). Take the arguments **before** you allocate memory or take a lock: in the library for the VM an
exception does not come back to your function (it jumps to the wrapper of the entry point), so what you hold then is lost. The copy of an array lives until the scope that called the native is left
(it is a temporary of the pool, like a string the native makes) - a native that keeps the bytes **beyond the call** (a queue, a texture, a module it parses) copies them, as it must for a buffer anyway
(the program may change or delete it). A native that reads *or* fills depending on an argument (a USB transfer: the direction bit) picks the function by it (`fire_usbb_bytes` in
`packages/usb/native/fire_bridge_usb.h`).

A buffer of machine words (`word a[]`) is no bytes - its size in bytes depends on the target: `bytesArg` and `bufferArg` refuse it (`<what> expects bytes (a byte
buffer or an array of ints from 0 to 255), not a word buffer.`); a native that wants words takes them with `wordsArg`:

```c
static Value fire_sk_Sum(Value data) {
    FireWords w;
    FireWord total = 0;
    uint32_t i;
    if (!wordsArg(data, "Sensorkit.Sum", &w)) return Undef();
    for (i = 0; i < w.length; i++) total = (FireWord)(total + w.data[i]);
    return WordV(total);   /* a word as the result (K_Word); a word argument arrives as one too (v.w) */
}
```

`FireWord` is `uint16_t`, `uint32_t` or `uint64_t` - the target's `FIRE_WORD_BITS`; a package library for the VM is built for the machine the VM runs on (64-bit words; the C ABI carries
word buffers as `FIRE_WORDS` and words as `FIRE_WORD`).

## 3. Platforms

* **A native build** compiles your source for the target. Use the macros the generated file defines: `FIRE_TARGET_LINUX`, `FIRE_TARGET_WINDOWS`, `FIRE_TARGET_ESP32`, ... (the name of the
  target) and `FIRE_HAL_POSIX`, `FIRE_HAL_WINDOWS`, `FIRE_HAL_FREERTOS` (the platform package). The platform package also gives you what the runtime needs from the system
  (`plat::` in `native/platform/<name>/fire_platform.hpp`: clock, sleep, ...), and the target defines the includes of its SDK (`#include <freertos/FreeRTOS.h>` ...).
* **Per-platform files**: `native.platformSources` adds files for one platform or target, so that the code for each system stays in its own file. They are included **before** `sources`, so
  the common code in `sources` can use what they declare:

  ```json
  "native": {
    "sources": [ "sensor.hpp" ],
    "platformSources": { "posix": [ "sensor_posix.hpp" ], "windows": [ "sensor_win.hpp" ], "esp32": [ "sensor_esp.hpp" ] },
    "platforms": [ "posix", "windows", "esp32" ],
    "functions": [ ... ]
  }
  ```

  The key is the platform package (`posix`, `windows`, `freertos`, `dos16`) or the name of a target (`linux`, `macos`, `esp32`, ...). Two keys are special:
  `default` is taken for a platform that has no key of its own, and `vm` is the layer of the library for the virtual machine (see below). `platforms` lists where
  the C++ may be used: a build for another target is refused with a clear message (empty: everywhere; with `default`, everywhere as well).
  So a package brings the code it needs on each system itself and updates it with the package - the standard packages do so: `io` (the file system: `fire_fs_*.h`), `audio`, `net`, `tls`, `devices`, `gpio`, `i2c`, `spi`, `usb`, `wifi`, `windows` (the display), `console`, `machine`, `dos` and `xms` (`fire_console_dos.h`, `_posix.h`, `_win32.h`; `vm` and `default`: `fire_console_none.h`).
* **Headers: files that a source includes.** A selector of a platform often includes the implementation it picks (`#if defined(__APPLE__)` / `#include "sensor_mac.h"`),
  which includes common parts. List such files in `native.headers`: they are files of every build (`pkg/<import>/` next to the generated program, the headers of a
  used import there as well), and an `#include` of a header in a source names that file - so the selector works as written, also inside `#if`, and the program only
  gets the text it compiles. A header that is a source of the build is taken out as before (it is in the file already). Give every header an include guard. The
  windows package is the example: `platformSources` names one file per platform (`fire_display_select.h` for `posix` and `windows`: fire's own display,
  which picks X11, Wayland, Win32 or Cocoa), `headers` all the others. Headers are packed with the package. (In a project that is not installed, the headers are
  put in where they are included.)

  ```json
  "native": {
    "sources": [ "native/sensor_bridge.h" ],
    "platformSources": { "posix": [ "native/sensor_select.h" ], "esp32": [ "native/sensor_esp.h" ] },
    "headers": [ "native/sensor_select.h", "native/sensor_linux.h", "native/sensor_mac.h", "native/sensor_common.h" ]
  }
  ```
* **Using the platform layer of another package**: `native.uses` names imports whose `platformSources` this package needs as well - the layer of the other package
  is put before the own sources (its include guards keep it to one copy when both packages are imported). `graphics` reads its fonts and images through the file system of `io`:

  ```json
  "native": { "sources": [ ..., "native/fire_bridge_graphics.h" ], "language": "c", "uses": [ "io" ] }
  ```

  The shared part belongs to the package whose core it is; the others reference it, so it is updated in one place. The used package must be installed.
  An entry can name the platforms it is for (the keys of `platformSources`, `vm` for the library of the virtual machine) - circuits plays through the sound of
  audio only where it has some: `"uses": [ { "import": "audio", "platforms": [ "posix", "windows", "vm" ] } ]`.
* **The virtual machine** runs on the machine it is on: the library is built for it (Windows: `windows`, Linux and macOS: `posix`) with its `platformSources` -
  unless the package has the key `vm`: then the library gets only that layer (a package whose code reaches hardware or a console the VM does not have says
  "none" there, and the program sees the same behaviour as natively without that hardware).
* **`#include` and the comment after it.** `#include "x.h"  /* why */` is fine: the line counts as an include of `x.h`.
* **Build order.** All sources of a build - first the `platformSources` of the target, then `sources` - are put into **one** file, one after the other. An `#include "x.h"` of another
  source of the same import is taken out there (the source is in the file already), so whatever a source includes has to come **before** it. By default that is the order of the lists;
  an entry can say it explicitly with an order value - the sources are sorted by it (smaller first, stable; an entry without one has order 0 and keeps its place among the others):

  ```json
  "sources": [
    "native/sensor_bridge.h",
    { "file": "native/sensor_types.h", "order": -10 },
    { "file": "native/sensor_tables.h", "order": -5 }
  ]
  ```

  The same works in `platformSources` (one sort over the whole build, so a common header can come before a platform file). The same order holds for the library of the
  virtual machine, a native build (also a library module of linkage `static`/`shared`) and plugins. **Checked:** a source that includes another source of the import that comes after it is an
  error when the package is packed (`ember pack`, Pack Library; for every platform of `platformSources`) and when it is built, with the file and the line:
  `native/sensor_bridge.h:3: #include "sensor_types.h" is a source of the same package that is built after it - give 'native/sensor_types.h' a smaller build order ("order") than 'native/sensor_bridge.h', or list it before.`
  (The includes are not resolved automatically on purpose: the order is written down, and the editor shows and sets it - *Properties* of the file, [Projects and solutions](../tools/projects.md).)

## 4. The virtual machine: what crosses the C boundary

In the VM your functions run in a shared library, so values are **copied** at the boundary:

* numbers, `bool`, `char`, text, byte buffers and **arrays of these** (also nested) go in and out;
* an array you receive is a copy: changing it does not change the caller's array - return the new one;
* a **byte buffer you receive is the caller's own memory** (no copy): the VM pins it for the call, you read and write the bytes in place (`b->bytes()`), exactly like in a native build, where a buffer
  never moves either. So a native can fill a read buffer (`Read(handle, buffer, ...)`) or draw into a framebuffer of any size without a copy; the length is fixed. Buffers of 16 KiB and more live on the
  pinned heap of .NET, so they keep their address for their whole life. A buffer you *return* is copied;
* objects, lambdas and pointers cannot cross (an error says so); units are not carried;
* an error inside your function (`fatal`, `indexError`, ...) is reported to the VM with its text (it ends the program there, like an error of a built-in native); `fireError("Class", "text")` is reported
  with the class (ABI result 2: `error` holds the class name, a line feed and the message) and the VM throws that exception, so a script can `catch` it;
* **the host's decisions** reach a library through callbacks (`fire_host`, `fire_pkg_set_host` in `native/abi/fire_pkg_abi.h`): which paths a script may touch (`io_allow`, the `IoPolicy` of the host) and where the
  console goes (`std_read`/`std_write`/`std_flush`, the `IoStdio` of the host, e.g. the output window of the editor). In C++ they are `libraryHost()->io_allow(...)` etc.; the io package is the example
  (`fire_io_allowed()` and the console functions in `packages/io/native/fire_bridge_io.h`). When a program ends the VM calls `fire_pkg_reset` - the function named by `native.reset` in the manifest (`"reset": "fire_io_reset"`): forget what the program
  left behind (open streams), the library stays loaded for the next program;
* **the network**: `net_allow(host, port, access, ...)` asks whether a script may connect to, listen on or resolve a host (`NetPolicy` of the host); the net package is the example (`fire_net_allowed()` in `packages/net/native/fire_bridge_net.h`, its sockets come with the package:
  `fire_net_*`, `packages/net/native/fire_net_sockets.h`);
* **windows**: `win_attach(framebuffer, present, ctx)` lets a package draw into the windows that show a framebuffer itself: the host calls `present` (a `fire_present_fn`) on the window's
  thread with the native handles of the window (`fire_native_window`: Win32, X11, Wayland, a CAMetalLayer) and the framebuffer's pixels to lay over its picture, instead of showing the
  framebuffer; `win_detach` ends it. The scene3d package is the example (DirectDrawing, `s3d_gpu_present` in `packages/scene3d/native/s3d_gpu_vk.h`);
* **devices**: the `dev_*` callbacks of `fire_host` give a library the device manager of the host (its drivers, handles, the shared devices of the editor, the packet trace). The devices package
  uses them through a driver "host" (the devices of kind `FIRE_DEVB_HOST` in `packages/devices/native/fire_bridge_devices.h`); a native build uses the serial and loopback drivers in C instead. Waiting that must be abortable
  and know the program's `#timeout` is a `host` function (`WaitFor`): the VM waits and asks the library once per step (`__DEVWaitStep`);
* calls into one library are serialized (the runtime inside is not thread-safe).

The library is built **by the compiler at first use** with a C++ compiler of the machine (g++, clang++ or MSVC `cl`; the first run of a script that imports the package takes a few
seconds) and kept in `Packages\<name>\lib\<runtime identifier>\`. A package can bring prebuilt libraries instead, so that users need no compiler:

```json
"native": { "sources": [ "..." ], "libraries": { "win-x64": "bin/win-x64/sensorkit.dll", "linux-x64": "bin/linux-x64/libsensorkit.so", "osx-arm64": "bin/osx-arm64/libsensorkit.dylib" }, "functions": [ ... ] }
```

A prebuilt library is used as it is when it is for the machine (runtime identifier `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, ...). It can be written in any language as long as it
exports the entry points of `native/abi/fire_pkg_abi.h`:

```c
int fire_pkg_abi_version(void);                                   /* 1 */
int fire_pkg_function_count(void);
const char* fire_pkg_function_name(int index);                    /* the name of "name" in the package description */
int fire_pkg_function_arity(int index);
int fire_pkg_call(int index, const fire_val* args, int argc, fire_val* result, char* error, int errorSize);   /* 0 = ok */
```

A **packed standalone program** (`fire.Compiler build`, the VM inside) carries the libraries of its packages in its payload and unpacks them when it starts - on every operating system, the same way as the
libraries of the built-in bridges (graphics and windows, serial ports). A program is packed for the machine that packs it: pack on the system you want to run it on, or build natively
(`--engine native`), which needs no libraries at all.

### Tracing calls (measuring a native outside the VM)

To measure or profile the C++ of a package without the virtual machine around it, the calls a program makes can be recorded and played back:

* `FIRE_PKG_TRACE=<file>` writes a line per call of a package native - its name and its arguments (numbers exactly, text, arrays; of a byte buffer only the length) -
  and `FIRE_PKG_TRACE_PREFIX=__Cir` limits it to the natives whose names start with that.
* `FIRE_PKGBUILD_KEEP=1` keeps the folder in which a package library is built (the generated `wrapper.cpp`, the runtime, the ABI headers), so the library can be
  built again by hand - with other flags, with `-g` for a profiler.

A small program that loads the library (`fire_pkg_call`) and calls the recorded functions again is then enough to time them or run them under valgrind; the
kernel of the circuits package was measured this way.

## 5. The C++ toolchain (for users)

A native build and the natives of packages need a C++ compiler on the machine. fire looks for one in this order: the toolchain chosen for the machine (`Toolchain\toolchain.json` next to
the program), the portable toolchains in `Toolchain\` next to the program (**w64devkit** first), well-known places (w64devkit, MSYS2, MinGW, LLVM) and the PATH (`g++`, `clang++`, `cl`).

When there is none, nothing fails silently - the user is told *why* a compiler is needed ("The package 'x' contains native code (C++) ..." or "A native build translates the program to
C++ ...") and offered:

* **Install the toolchain automatically** (Windows x64): the portable w64devkit (a GCC, about 80 MB) is downloaded from its GitHub release and unpacked to `Toolchain\w64devkit\` next to the
  program. Nothing is installed on the system; it is configured automatically (the machine toolchain is set to it).
* **Change the toolchain...** (editor): the toolchain settings open (*Tools > Toolchain Settings > Toolchain*); afterwards the operation is tried again, and the question comes again if there is still none.
  The settings have **Detect** (finds a toolchain on the machine, w64devkit first) and **Test** (compiles and runs a small program with the toolchain as set up).
* **Cancel**: stops the operation.

On the command line the same question is a text: `Fix automatically? [F]ix / [C]ancel (f):` (only when somebody can answer; in scripts and CI the build just reports that there is no compiler).
On Linux and macOS the toolchain has to come from the package manager (`g++`/`clang++`, `xcode-select --install`): the message says so.

## 6. Checklist

1. The function is `inline Value name(Value ...)` (and `OwnList* list` last, if it allocates) in `namespace fire`, in a header that includes what it uses.
2. Everything you allocate for the result is allocated in `list`.
2b. Byte arguments go through `bytesArg` (read) or `bufferArg` (filled) - never `bufOf` unchecked (section 2, "Bytes as arguments").
3. The prelude wraps the natives; the natives are called by their package names.
4. Code for one system lives in `platformSources` or behind `FIRE_TARGET_*` / `FIRE_HAL_*`.
5. Try both engines: `fire.Compiler run script.fire` (VM, library) and `fire.Compiler build script.fire --engine native -t linux`.
