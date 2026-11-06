# macOS

The VM (spark, `forge`) and native programs (`-t macos`, the default on a Mac).

## Setup

- The Xcode command line tools (`xcode-select --install`) give clang for native builds and the natives of packages.
- For `tls`: OpenSSL from Homebrew or MacPorts, with its `include` and `lib` folders in the toolchain (`includeDirs`, `libs` in
  [`fire.native.json`](README.md#firenativejson)) - or mbedTLS with `FIRE_TLS_MBEDTLS`.

## First program

```fire
print("Hello from macOS")
```

```bash
forge run hello.fire
forge build hello.fire --engine native -o hello
./hello
```

## Specifics

- **Windows:** Cocoa. Windows may live only on the main thread: there a window manager's thread only gives the beat, and the program shows the windows
  at its safe points - nothing changes for the program.
- **Sound:** Audio Queue Services (AudioToolbox).
- **System theme:** dark mode (`AppleInterfaceStyle`), accent, font (SF Pro), "reduce transparency".
- **Not available:** dragging out of a window into other programs (`window.CanStartDrag()` is false).
- The macOS layers are written against the documented APIs and checked by compiling; they have not been tried on a Mac yet.
