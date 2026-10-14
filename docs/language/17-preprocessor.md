# Directives, warnings and documentation comments

Lines that start with `#` are **directives**: they are handled before or while the program is compiled and are not statements of the program.

## Libraries and files

| Directive | Meaning |
|---|---|
| `#import "name"` | switches on a library of the standard library or of an installed package (`"time"`, `"io"`, `"linq"`, ...) - see [Standard library](19-standard-library.md) |
| `#include "file.fire"` | inserts the file at this place (relative to the including file). Every file is inserted only once; circular includes are an error. An `.fxml` file is translated into fire code first (UI markup) |
| `#using Name` | short type names of a namespace - see [Enums and namespaces](10-enums-and-namespaces.md#using) |
| `#extern "library"` | the following `extern` declarations call functions of a native library - see [Native code](18-native-code.md) |

## Conditional compilation

```fire
#define FAST
#ifdef FAST
print("fast")
#else
print("slow")
#endif

#if linux && vm
print("linux in the VM")
#elif windows
print("windows")
#endif
```

| Directive | Meaning |
|---|---|
| `#if expr`, `#elif expr`, `#else`, `#endif` | choose a branch; can be nested |
| `#ifdef NAME`, `#ifndef NAME` | the same for one symbol |
| `#define NAME`, `#undef NAME` | add or remove a symbol |
| `#error text` | in a taken branch: stops the compilation with `text` |

`expr` uses symbol names, `true`, `false`, `!`, `&&`, `||` and parentheses; names are case-insensitive, an unknown name is false. The lines of a branch
that is not taken are never compiled - they may use libraries that do not exist on the target. The editor greys them out.

The build defines these symbols:

| Symbol | When |
|---|---|
| `windows`, `linux`, `macos`, `posix` | the operating system of the target (`posix` for Linux and macOS) |
| `esp32`, `freertos` | the board of a native target |
| `vm`, `native` | the engine: the virtual machine or a native (C++) build |
| `float32` | `float` has 32 bits |
| your own | `-D NAME` on the command line, `symbols` of a target in `fire.native.json`, `#define` |

## Settings of the program

| Directive | Meaning |
|---|---|
| `#strict`, `#lax` | type checking mode of this file - see [Types](02-types.md#the-type-checker-lax-and-strict) |
| `#debug`, `#performance` | execution mode of the VM: `#debug` checks everything and allows stepping, `#performance` skips run-time checks (default: release) |
| `#floatwidth 32` | `float` without explicit width has 32 bits |
| `#timeout 10s` | default waiting time of the waiting functions of devices and network |
| `#nosync` | threads may change globals only at an explicit `sync globals` - see [Threads and actors](15-threads-and-actors.md#global-variables) |
| `#noshadow` | threads do not see the globals of the main program |
| `#noconsole` | a built program for Windows opens no console window |

## Information about the program

These directives describe a program that is built into an executable file (shown in the file properties on Windows): `#name "..."`,
`#description "..."`, `#author "..."`, `#codename "..."`, `#comments "..."`, `#version "1.2.0"`, `#fileversion "1.2.0.0"`, `#icon "app.ico"`. In a
project, the project settings provide the same information.

## Warnings in the code: `warn`

`warn <number>: "text"` is a warning of the programmer. The compiler reports it as `W<number>` and compiles nothing for it:

```fire
class Old {
    undefined Run() {
        warn 204: "Run is obsolete - use Start()"
        print("run")
    }
}
new Old().Run()     // warning W204: Run is obsolete - use Start() (Old.Run)
```

Where it stands decides where it is reported: in top-level code at that line; in a class body at every `new` and static use of the class; in a method,
constructor or property at every call or access. The number is from 0 to 99999.

Errors of the compiler have codes too (`E4022: Unknown identifier 'x'`), as do the findings of the type checker (`TC001` ...).

## Documentation comments

Lines starting with `///` directly above a class, interface, field, property, method or constructor document it. The editor shows the text as a tooltip
while you type and when the mouse rests on the name.

```fire
/// A circle with a radius.
class Circle {
    /// The radius, in millimeters.
    float radius

    /// <summary>Scales the circle.</summary>
    /// <param name="factor">What the radius is multiplied by.</param>
    /// <returns>The new radius.</returns>
    float Scale(float factor) {
        this.radius = this.radius * factor
        return this.radius
    }
}
print(new Circle().Scale(2.0))
```

- The `///` lines must follow each other directly and stand directly above the declaration.
- Plain text is the summary; a blank `///` line starts a new paragraph.
- The tags `<summary>`, `<param name="...">`, `<returns>` and `<remarks>` are understood; `<c>`, `<para>` and `<see cref="..."/>` are shown as text.

## Exercises

1. Write a program that prints `"debug build"` only when it is compiled with `-D DEBUG`.
2. Mark a method as obsolete with `warn` and call it from two places - how often is the warning reported?
3. Document a class with `///` and hover over its name in the editor.
4. Split a program into two files and join them with `#include`.
