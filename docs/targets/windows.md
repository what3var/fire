# Windows

The VM (spark, `forge`) and native programs (`-t windows`, the default on Windows).

## Setup

spark needs nothing but itself (it is built self-contained). A native build - and the natives of packages in the VM - need a C compiler:

- **spark installs one for you:** at the first native build without a compiler it offers to download **w64devkit** (GCC for Windows) into
  `Toolchain\w64devkit\` next to the program - portable, nothing is installed system-wide. *Tools > Toolchain Settings* shows what is used.
- Otherwise the compiler is looked for in `Toolchain\`, well-known places and the `PATH`: GCC (MinGW), clang, or `cl` (msvc, from a Developer
  Command Prompt).

## First program

```fire
print("Hello from Windows")
```

```bash
forge run hello.fire
forge build hello.fire --engine native -o hello.exe
hello.exe
```

`#noconsole` (or the project setting) makes a program without a console window - for a program with windows only.

## Specifics

- **Windows:** Win32 windows, presented through DirectComposition (transparent backdrops: Mica, Acrylic on Windows 11). Dark mode, accent colour,
  font (Segoe UI) and DPI come from the system.
- **Sound:** winmm (`waveOut`, `waveIn`), nothing to install.
- **TLS:** SChannel - the certificate store of Windows, nothing to install.
- **USB:** WinUSB - the device needs the WinUSB driver (Zadig, or a WinUSB descriptor in the device).
- **Program information:** `#name`, `#description`, `#author`, `#version`, `#fileversion`, `#icon "app.ico"` go into the executable
  ([directives](../language/17-preprocessor.md)).
- **msvc** builds with `linkage: static` at most; `shared` and plugins need MinGW (GCC or clang).
- Cross-building from Linux works with MinGW (`x86_64-w64-mingw32-gcc` as the toolchain's compiler); the test suite runs such programs under Wine.
