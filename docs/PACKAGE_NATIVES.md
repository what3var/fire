# Writing the natives of a package (C++) and connecting them to the prelude

A package (docs/PACKAGES.md) can bring **natives**: functions written in C++ that fire code calls. This is how a package reaches an operating system API, a sensor, a
protocol stack or a fast algorithm. You write the C++ **once**; the compiler uses it in both engines:

| engine | what happens to your C++ |
|---|---|
| native build (`fire.Compiler build --engine native`, any target: Linux, Windows, macOS, ESP32, FreeRTOS, ...) | the text of your source files is put into the generated C++ file; your functions are called directly |
| virtual machine (`fire.Compiler run`, the editor, a packed standalone program) | the compiler builds your sources into a **shared library** (`.dll`, `.so`, `.dylib`) for the machine, with a generated wrapper that exports a small C interface (`native/abi/fire_pkg_abi.h`); the VM loads it and calls it |

So the C# VM never needs to know your functions: it only knows the C ABI, and the wrapper is generated for you.

## 1. The three parts

```
sensorkit/
  sensorkit.fire     the prelude: fire code that wraps the natives in classes
  sensorkit.hpp      the natives: C++ functions on Value
  sensorkit.json     the forge file (ember forge sensorkit.json)
```

### The native (`sensorkit.hpp`)

A native takes `Value`s and returns a `Value`. The file is put at the top level of the generated file after the runtime (`fire_rt.hpp`), so it includes what it needs itself
and puts its functions into `namespace fire`:

```cpp
#include <cstdint>

namespace fire {

// crc8(buffer) -> int
inline Value sk_crc8(Value buffer) {
    Buf* b = bufOf(buffer);                       // a byte buffer
    uint8_t crc = 0;
    for (uint32_t i = 0; i < b->length; i++) {
        crc ^= b->bytes()[i];
        for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x07) : (uint8_t)(crc << 1);
    }
    return Int(crc);
}

// hex(buffer) -> string        (takes the list of the scope: it allocates the string)
inline Value sk_hex(Value buffer, OwnList* list) {
    Buf* b = bufOf(buffer);
    Str* s = allocStr(b->length * 2, list);
    static const char digits[] = "0123456789abcdef";
    for (uint32_t i = 0; i < b->length; i++) {
        strChars(s)[2 * i] = digits[b->bytes()[i] >> 4];
        strChars(s)[2 * i + 1] = digits[b->bytes()[i] & 15];
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
    static Crc8(buffer) { return __sk_crc8(buffer) }
    static Hex(buffer) { return __sk_hex(buffer) }
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
          { "name": "__sk_crc8", "arguments": 1, "cpp": "sk_crc8" },
          { "name": "__sk_hex",  "arguments": 1, "cpp": "sk_hex", "needsList": true, "returnsReference": true }
        ]
      }
    }
  ]
}
```

* `name` is what fire code calls, `cpp` the C++ function, `arguments` how many `Value`s it takes.
* `needsList: true`: the function gets `OwnList* list` as an extra **last** parameter. Everything you allocate for the result (`allocStr`, `allocArr`, `allocBuf`)
  is allocated in that list: it belongs to the scope of the caller, like any value a fire function creates. `returnsReference: true`: the result is such a value.
* `exceptions` (in `native`): the exception classes of your prelude that your natives throw, e.g. `"exceptions": [ "SensorException" ]`. The class needs a constructor with one text
  argument; the native throws it with `return fireError("SensorException", "no answer");` (a native build constructs the class by name, the VM gets the class and the text from the library).
* `host: true` (per function): in the virtual machine the host runs the function itself instead of the library. It is for what the C ABI cannot carry or what must not run inside a library:
  a wait that has to be abortable (`Sleep`), or a function that needs the unit of a value. The VM looks the function up by name in its own table (`fire.Runtime.HostNatives`), so this is for the
  standard packages; in a native build the `cpp` function is used as always.
* Then: `ember forge sensorkit.json`, `ember install build\sensorkit-1.0.0.fpk`, and in a script `#import "sensorkit"`.

## 2. The value API

Everything is in `native/runtime/fire_rt.hpp` (the runtime that the generated file includes). The parts you need most:

| | |
|---|---|
| make | `Int(i)`, `Float((Real)x)`, `Bool(b)`, `Char(c)`, `Undef()`; strings `Str* s = allocStr(length, list)` then `strChars(s)[i] = ...` and `StrV(s)`; arrays `Arr* a = allocArr(n, list)`, `a->items()[i] = v`, `ArrV(a)` (call `retain(v)` for a string you store); buffers `Buf* b = allocBuf(n, list)`, `b->bytes()`, `BufV(b)` |
| read | `v.kind` (`K_Int`, `K_Float`, `K_Bool`, `K_Char`, `K_String`, `K_Array`, `K_Buffer`, `K_Undefined`), `v.i` (int, bool, char), `v.f` / `toR(v)` (a number as a real), `strOf(v)->length` and `->data` (UTF-16), `arrOf(v)->length` / `->items()`, `bufOf(v)->length` / `->bytes()` |
| errors | `return indexError("Array index", i, length);` throws an `IndexOutOfBoundsException` in a native build; `fatal("text")` ends the program (native build) or reports an error to the VM (library). Check your arguments (`v.kind`) and fail with `fatal`; to let the script catch the error, throw an exception of your prelude: `return fireError("SensorException", "text");` (list the class in `exceptions`). |

`Real` is `double`, or `float` with `#floatwidth 32`. A value of another kind than you expect is your bug to catch: the generated code does not check the kind of the arguments for you.

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

  The key is the platform package (`posix`, `windows`, `freertos`) or the name of a target (`linux`, `macos`, `esp32`, ...). `platforms` lists where the C++ may be used:
  a build for another target is refused with a clear message (empty: everywhere).
* **The virtual machine** runs on the machine it is on: the library is built for it (Windows: `windows`, Linux and macOS: `posix`) with its `platformSources`.

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
libraries of the built-in bridges (SDL, serial ports). A program is packed for the machine that packs it: pack on the system you want to run it on, or build natively
(`--engine native`), which needs no libraries at all.

## 5. The C++ toolchain (for users)

A native build and the natives of packages need a C++ compiler on the machine. fire looks for one in this order: the toolchain chosen for the machine (`Toolchain\toolchain.json` next to
the program), the portable toolchains in `Toolchain\` next to the program (**w64devkit** first), well-known places (w64devkit, MSYS2, MinGW, LLVM) and the PATH (`g++`, `clang++`, `cl`).

When there is none, nothing fails silently - the user is told *why* a compiler is needed ("The package 'x' contains native code (C++) ..." or "A native build translates the program to
C++ ...") and offered:

* **Install the toolchain automatically** (Windows x64): the portable w64devkit (a GCC, about 80 MB) is downloaded from its GitHub release and unpacked to `Toolchain\w64devkit\` next to the
  program. Nothing is installed on the system; it is configured automatically (the machine toolchain is set to it).
* **Change the toolchain...** (editor): the toolchain settings open (*File > Native Build Settings > Toolchain*); afterwards the operation is tried again, and the question comes again if there is still none.
  The settings have **Detect** (finds a toolchain on the machine, w64devkit first) and **Test** (compiles and runs a small program with the toolchain as set up).
* **Cancel**: stops the operation.

On the command line the same question is a text: `Fix automatically? [F]ix / [C]ancel (f):` (only when somebody can answer; in scripts and CI the build just reports that there is no compiler).
On Linux and macOS the toolchain has to come from the package manager (`g++`/`clang++`, `xcode-select --install`): the message says so.

## 6. Checklist

1. The function is `inline Value name(Value ...)` (and `OwnList* list` last, if it allocates) in `namespace fire`, in a header that includes what it uses.
2. Everything you allocate for the result is allocated in `list`.
3. The prelude wraps the natives; the natives are called by their package names.
4. Code for one system lives in `platformSources` or behind `FIRE_TARGET_*` / `FIRE_HAL_*`.
5. Try both engines: `fire.Compiler run script.script` (VM, library) and `fire.Compiler build script.script --engine native -t linux`.
