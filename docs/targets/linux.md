# Linux

The VM (spark, `forge`) and native programs (`-t linux`, the default on a Linux machine).

## Setup

| What | Package (Debian/Ubuntu) | Needed for |
| --- | --- | --- |
| .NET 10 SDK | `dotnet-sdk-10.0` | building fire itself (`dotnet build`) |
| a C compiler | `gcc` (or `clang`, `tcc`) | native builds, and the natives of packages in the VM (built once per package, then cached) |
| OpenSSL headers | `libssl-dev` | `tls` / `https://` - only where the library is built |
| fonts | e.g. `fonts-noto-core`, `fonts-dejavu-core` | the font of the UI (else the built-in bitmap font) |

Windows, sound, USB and the system theme need nothing at build time: X11 (`libX11`, `libXext`, `libXrandr`...), Wayland (`libwayland-client`,
`libxkbcommon`), PulseAudio / ALSA and libusb are loaded with `dlopen` when a program first uses them. A program runs on a machine with any of them
or none (it then says "not supported" for what is missing).

## First program

```fire
print("Hello from Linux")
```

```bash
forge run hello.fire
forge build hello.fire --engine native -o hello
./hello
```

The native build uses the first compiler it finds (`gcc`, `clang`; TinyCC with `--toolchain tcc` - a program with packages in C is pure C).

## Specifics

- **Windows:** Wayland when `WAYLAND_DISPLAY` is set (the window draws its own title bar), else X11. `FIRE_DISPLAY=x11` forces X11. Without a
  display a window cannot open: that is an error message, and the window counts as closed. For tests and servers `FIRE_DISPLAY=headless`; to look
  at windows on a machine without a screen, run under Xvfb (`xvfb-run forge run app.fire`).
- **System theme:** GNOME (`gsettings`: dark mode, accent, font, scaling) or KDE (`~/.config/kdeglobals`); `GDK_SCALE`/`QT_SCALE_FACTOR` win.
- **Sound:** PulseAudio (also PipeWire) when a server answers, else ALSA (`"alsa:plughw:1,0"`).
- **Hardware:** GPIO through `/dev/gpiochipN` (kernel 5.10+), I2C through `/dev/i2c-N`, SPI through `/dev/spidevB.C` - only when the kernel headers
  were there at build time. The user must be in the groups `gpio`, `i2c`, `spi` (Raspberry Pi: enable the interfaces). USB through libusb.
- **Plugins** and `linkage: shared`: shared libraries next to the program (`rpath $ORIGIN`).
