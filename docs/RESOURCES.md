# Resources: files inside the program

```
var logo = new Resource("images/logo.png")        // the path is relative to the source file that writes it
var image = Framebuffer.FromResource(logo)        // needs #import "graphics"
print(new Resource("texts/license.txt").Text())
```

`new Resource("path")` is **resolved when the program is compiled**: the preprocessor reads the file, stores its bytes in the program
(`CompiledProgram.Resources`) and replaces the path by the number of the resource - the program that is run contains `new Resource(3)`. Nothing is read from
the file system at run time, so the program does not depend on the place it is started from:

| where the program runs | where the bytes are |
|---|---|
| the virtual machine (editor, `fire run`) | the resource table of the compiled program, filled by the compiler |
| a packed standalone file | the same table, serialized with the program (the payload of the file) |
| a native build | `static const uint8_t` arrays in the generated C++ file - the bytes are part of the binary |

## Rules

- The argument must be a **string literal**: `new Resource(path)` with a variable cannot be resolved and is an error at run time
  (*A Resource needs a path that is a string literal*). Neither a `//` comment nor a string that contains `new Resource("...")` is touched.
- The path is relative to the **source file that mentions it** (also in an `#include`d file); an absolute path works too. A file that does not exist is a
  compile error with the line.
- The same file is stored **once**, however often it is mentioned (`new Resource("a.png")` twice gives two objects with the same `id`).
- The resources are part of the program: a large file makes the program (and a native binary) large.

## Resources of libraries and packages

A library (a project of type `library`) uses resources like a program: `new Resource("../icons/up.png")` relative to its file. When it is **packed**
(`forge build`, *Pack* in spark), the paths are rewritten relative to the project folder (a file outside of it becomes `external/name`) and the files go
into the `.fpk`, next to the prelude (`imports[].resources` in the `package.json`: the path as the prelude writes it -> the file in the package). Installed,
the prelude is read from its own folder: a program that `#import`s the package gets the files embedded like its own, in the virtual machine, a packed file and
a native build. A file that does not exist is an error when the library is packed.

## The class `Resource` (always available)

| member | |
|---|---|
| `Name()` | the path as it was written in the source |
| `Length()` | the number of bytes |
| `Bytes()` | the content as a `byte` buffer (a copy) |
| `Text()` | the content as text (UTF-8) |
| `id` | the number of the resource |

Libraries take resources where they take files: `Framebuffer.FromResource(resource)` (PNG, BMP, GIF). `Fonts.Add(resource)` reads a TrueType font (docs/FONTS.md).
