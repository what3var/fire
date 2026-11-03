# Native code

fire programs can call functions of native libraries, work with pointers in `unsafe` blocks and be translated into native C++ programs.

## Calling a native library: `extern`

`extern` declares the signature of a native function; `#extern "library"` says which library the following declarations come from:

```fire
#if linux
#extern "libc.so.6"
extern int abs(int n)
extern int strlen(string s)
print(abs(-12))         // 12
print(strlen("hello"))  // 5
#endif
```

- On Windows the library is e.g. `"kernel32.dll"` or `"user32.dll"`, on Linux `"libc.so.6"`, on macOS `"libSystem.dylib"`.
- Parameter and return types: `bool`, `int` (with widths: `int[32]`), `float`, `char`, `string` and pointers. A function without a return type returns `undefined`.
- The library is loaded at the first call; a function that cannot be found is an error at the call, not when compiling.
- A host program that embeds fire can also provide `extern` functions itself (see the help page "Embedding").

### Loading a library while the program runs: `Library.Load`

`extern` names its functions when the program is written. `Library.Load(path)` loads a native library (C ABI) while the program runs - a path, or a
name the system finds - and `Get(name, signature)` gives one of its functions as a lambda:

```fire
#if linux
var libm = Library.Load("libm.so.6")
var cos = libm.Get("cos", "float(float)")
print(cos(0.0))                                     // 1
var add = Library.Load("./libmine.so").Get("add", "int[32](int[32], int[32])")
print(add(40, 2))                                   // 42
#endif
```

- The **signature** is the result type, then the parameter types in parentheses, in C order: `"int[32](string, float)"`, `"void(int*)"`, `"string()"`.
  The types are those of `extern`: `bool` (a C `bool`), `int` (64 bits) and `int[8]`/`int[16]`/`int[32]`/`int[64]`, `float` (a C `double`) and `float[32]`
  (a C `float`), `char` (a UTF-16 unit), `string` (UTF-8 both ways: passed as `const char*`, a returned `const char*` is copied and not freed - see `release:`
  below), pointers with `*` (a pointer of `unsafe`, an address as `int` or a `NativeHandle`; `void*` too) and `void` as the result. At most 8 parameters.
- `lib.Path` is what it was loaded from. A library is not unloaded.
- Errors are a `LibraryException`: the library cannot be loaded, it has no function of that name, the signature is not one.

#### Freeing what a library returns: `release:`

Many C functions return memory or a resource that the caller must give back with another function of the same library (`fopen`/`fclose`, `strdup`/`free`,
`sqlite3_mprintf`/`sqlite3_free`, `SDL_GetClipboardText`/`SDL_free`). `Get(name, signature, release: "freeFunction")` names it - the library's own
function, looked up in the same library (a `void(void*)`; each Windows DLL may have its own C runtime, so it is never the program's `free`):

```fire
#if linux
var libc = Library.Load("libc.so.6")
var fopen = libc.Get("fopen", "void*(string, string)", release: "fclose")
var fputs = libc.Get("fputs", "int[32](string, void*)")
{
    var f = fopen("notes.txt", "w")
    if (f == undefined) { print("cannot open") }
    else { fputs("hello\n", f) }
}                                                  // the block ends: fclose(f)

var strdup = libc.Get("strdup", "string(string)", release: "free")
print(strdup("copied"))                            // copied - the C copy is already freed
#endif
```

- A returned **pointer** becomes a `NativeHandle`, an object that follows [ownership](13-ownership.md): `delete h` calls the release function at once,
  the end of its owner (the block, the object it belongs to, the global scope at the end of the program) calls it too - always exactly once. Afterwards
  the handle is destroyed: passing it to a function or reading `h.Address` throws a `DestroyedException`, and a second `delete h` does nothing (no double free).
- A function of the library that expects a pointer takes the handle (`fputs("...", f)`); `h.Address` is the address as an `int`.
- A returned NULL is `undefined`, not a handle - nothing to free.
- A returned **string** is copied into a fire string and the C memory is freed at once with the release function: there is nothing to delete.
- Without `release:` nothing changes: a pointer is an `int`, a string is copied and never freed.
- A release function the library does not have, or `release:` on a function that returns neither a pointer nor a string, is a `LibraryException` from `Get`.
- Do not `copy` or `flat` a handle: the copy would free the same pointer again.
- In a **native build** the call goes through a small function the compiler generates for the signature: the signature must be a text constant somewhere in
  the program (`Get("add", "int(int, int)")`). A signature that is put together while the program runs works in the VM; a native build says
  "The signature '...' is not known to this native build: pass it to Get as a text constant."
- The boards and the browser have no dynamic loader: `Load` throws there.

### fire plugins

A **fire plugin** is a set of fire classes built on their own and loaded by a running program with the same `Library.Load`. Its classes then belong to the
program: `Reflect.New("Kit.MyDriver", [])`, `Type.Of`, `is of`, `catch` and the calls through an interface or a base class find them.

```
// the interface: a library of the project (or a package) that the program and the plugin both import
namespace Api {
    interface IDriver { string Name() }
}

// the plugin (kit.fire, or a project with "linkage": "plugin"): forge build kit.fire --linkage plugin --host bin/app -o plugins/kit
#import "Api"
namespace Kit {
    class MyDriver : Api.IDriver { string Name() { return "my driver" } }
}

// the program ("linkage": "shared" for a native build)
#import "Api"
#import "reflection"
var kit = Library.Load("plugins/kit.fireplugin")
print(kit.Classes()[0])                    // Kit.MyDriver
Api.IDriver d = Reflect.New("Kit.MyDriver", [])
print(d.Name())                            // my driver
```

- `forge build ... --linkage plugin` (or `"linkage": "plugin"` in the project) builds the classes of the program's own files as a plugin: `<output>.so`
  (`.dll`, `.dylib`) for native programs and `<output>.fireplugin`, its bytecode, for the VM. `Library.Load` takes either file: the VM loads the bytecode, a
  native program the library next to it. The plugin's top level does not run; the static fields of its classes are set when it is loaded.
- A native program can load plugins when it is built with **linkage `"shared"`** (Linux, macOS, Windows with MinGW). The plugin is built for it:
  `--host <program>` takes the switches of the program and (Windows) the DLLs it imports from (`<program>.firehost`, written next to the program).
- The plugin is checked when it is loaded (only by hashes): the libraries and packages it was built against must have the same **interface** in the
  program (classes, fields, members - not the code of the methods), and a native plugin must be built for the same configuration of the runtime (switches,
  target, version of fire). Otherwise `Load` throws a `LibraryException` that says why ("The plugin 'Kit' cannot be loaded: it was built against another
  interface of the module 'Api' ..."). A program without linkage "shared" says that it cannot load plugins.
- `lib.Classes()` are the names of the plugin's classes (empty for a C library), `lib.IsPlugin` says whether it is one. Loading the same plugin again gives
  the same classes; a plugin is never unloaded.
- Not yet: plugins with units, with natives in C++ or with `class extends` of a class of the program or a library; in a native program JSON
  (`Json.Serialize`) of objects of a plugin's classes and named arguments in calls of the program on them; debugging a plugin.

### Functions that can fail: `try Name(...)`

Some functions of the host or of packages are registered as *tryable*: they can only be called with `try` and return `undefined` instead of
throwing when they fail or time out:

```
var value = try ReadSensor(500ms)
if (value == undefined) { print("timeout") }
```

## Pointers: `unsafe`

Inside an `unsafe { }` block, `&x` takes the address of a variable, field or array element and `*p` reads or writes through a pointer. A pointer
type is written with `*` after the type: `int* p`, `int[8]* bytes`.

```fire
int x = 5
unsafe {
    int* p = &x
    *p = 7
    print(*p)       // 7
}
print(x)            // 7
```

- `p + n` and `p - n` move by `n` **elements** (not bytes); `p1 - p2` is the distance of two pointers into the same array.
- A pointer to a variable behaves like a pointer to an array of one element. Reading outside the bounds throws an `IndexOutOfBoundsException`
  (not checked with `#performance`).
- Pointers are mainly for native functions that expect an address (`extern int GetValue(int* out)`).

For the common case "let a method change my variable" you do not need pointers - use a `ref` parameter
(see [Classes](08-classes.md#parameters)):

```fire
var arr = [1, 2, 3]
class U {
    static undefined Twice(ref int v) { v = v * 2 }
}
U.Twice(arr[1])
print(arr[1])       // 4
```

## Natives in packages

A package (installed with `ember`) can bring native functions written in C++ together with a fire prelude that declares them. A native build compiles
the C++ into the program; the VM uses a shared library that is built from the same source for the machine. See the documentation of packages
(`PACKAGES.md`, `PACKAGE_NATIVES.md`) in the repository.

## Native programs

The same fire program runs in the VM (in the editor, with debugger) and can be translated into C++ and compiled into a native program - for the
desktop, for the browser (WebAssembly) and for microcontrollers like the ESP32:

```
forge build program.fire --engine native -o program
forge build program.fire --engine native -t esp32
```

- The behaviour is the same in both engines; the conformance tests of the project check this.
- Differences: errors of the native program that the VM reports as exceptions can end a native program (e.g. a calculation with a value of the wrong
  type); `#if native` / `#if vm` select code per engine (see [Preprocessor](17-preprocessor.md#conditional-compilation)).
- `#floatwidth 32` and the bit widths (`int[8]`, `float[32]`) make the program fit small targets.

## Exercises

1. Call `getpid` (Linux, `libc.so.6`) or `GetCurrentProcessId` (Windows, `kernel32.dll`) and print the process number; use `#if` to select the
   right library.
2. Write a method that swaps two array elements once with `ref` parameters and once with pointers in an `unsafe` block.
3. Build a small program natively with `forge build --engine native` and compare its output with the VM.
