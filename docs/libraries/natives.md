# Libraries with natives

A library can bring code in C or C++ - to reach an API of the operating system, a chip, or for speed. There are three places for it.

## In a library project: the folder `native/`

A library project with `"native": { "language": "c" }` (or `"native": {}` for C++) has its natives in the folder `native/` of the project. Every
`static inline Value name(Value a, ...)` there (C++: `inline Value name(...)` in `namespace fire`) is the fire function `__name`; the library's fire code
wraps them in classes. In spark: *File > New > Project... > Native Library (C)* (or *(C++)*).

The step-by-step example (a library `Calc` with two functions, a program that uses it, VM and native build, packing) is in
[packages with natives](../packages/natives.md#quick-start-a-library-with-natives-in-c) - a library project and a package are the same thing there.
What to know:

- **VM:** the first run builds the natives into a shared library for the machine (it needs a C or C++ compiler; the result is cached). A packed program
  carries the library.
- **Native build:** the natives are compiled into the program (C into the program's C file, C++ into a file next to it).
- **Package:** the C/C++ files travel in the `.fpk`; every machine builds them itself.
- **Build order:** files in `native/` are built in name order, headers first. A file that needs another can give an order (`{ "file": "native/x.h",
  "order": -10 }` in `native.sources`, or *Build order* in the Properties window of spark).
- **Platforms:** `platforms`, `platformSources` and `linkLibraries` limit the files to systems and link libraries of the system (`"linux": ["m"]`).
- The rules for writing a native (the `Value` API, strings, buffers, errors, threads) are the same as for packages: [packages with natives](../packages/natives.md).

## In a plugin

A plugin may have natives too, with one difference between the languages:

| Language | In a plugin |
| --- | --- |
| **C++** (the plugin project's `native/` with `"native": {}`, or a C++ package) | built into the plugin's library (and for the VM into a library next to its `.fireplugin`). They are the plugin's own copy: their state is not the program's, even when the program imports the same package |
| **C** (a package or library with `"language": "c"`) | the **program's**: the plugin calls the natives that the program has. A program exports the C natives of every package it imports to its plugins |

So a plugin that needs natives of its own writes them in C++:

`Kit/Kit.fireproj`

```json
{
  "format": 1,
  "name": "Kit",
  "references": [ { "project": "../Api/Api.fireproj" } ],
  "settings": { "linkage": "plugin" },
  "native": {}
}
```

`Kit/native/kit.hpp`

```cpp
#pragma once
namespace fire {
inline Value kit_twice(Value a) { return Int(a.i * 2); }
}
```

`Kit/kit.fire`

```fire
#import "Api"

namespace Kit {
    class MyDriver : Api.IDriver {
        string Name() { return "my driver " + __kit_twice(21) }      // my driver 42
    }
}
```

Built and loaded as in [a plugin step by step](building.md#a-plugin-step-by-step). C natives in the plugin's own project are not supported yet: loading
fails with "it calls the native function ... in C, which this program does not export to plugins". An exception a plugin's native throws must be a class
the program knows (`ArgumentException`, a class of a library).

## Calling a C library directly

Without writing natives, a program can call a function of any C library:

```fire
#if linux
#extern "libc.so.6"
extern int abs(int x)
#endif

#if linux
print(abs(-5))                      // 5
#endif
```

or load one while it runs - `Library.Load(path)`, then `lib.Get("name", "int(int)")` returns a lambda; `release:` names the function that frees what
it returns. All of that, with pointers (`unsafe`) and `NativeHandle`, is in [native code](../language/18-native-code.md).
