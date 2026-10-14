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
