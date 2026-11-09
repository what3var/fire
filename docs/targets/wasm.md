# Browser (WebAssembly)

`-t wasm` builds a program that runs in a web page: **one `.html` file** with the WebAssembly inside - it opens by double click, without a web server.

## Setup

[Emscripten](https://emscripten.org) (emsdk): install it, then make `emcc` reachable (`source emsdk_env.sh` / `emsdk_env.bat`). The toolchain
`emscripten` belongs to the target.

## First program

```fire
print("Hello from the browser")
```

```bash
forge build hello.fire -t wasm -o hello.html
```

Open `hello.html`: `print` writes into the text area of the page and into the browser's console.

## Specifics

- **fire threads** are tasks of fire's own scheduler on the fibers of Emscripten (one thread of the browser): they take turns at the safepoints (a slice of
  about 10 ms) and while they wait; when every task waits, the page gets its turn.
- **`Sleep` gives the page its turn:** while the program sleeps (or waits for window events), the page draws and delivers input. A loop without
  `Sleep` or `Tick` freezes the page, as anywhere in a browser.
- **Windows** draw into the canvas of the page (the UI works, with touch); **sound** is Web Audio and starts with the user's first click or key;
  **files** live in memory as long as the page is open; the system theme follows `prefers-color-scheme` and `prefers-reduced-transparency`.
- **Not available:** network, TLS, devices, GPIO/I2C/SPI/USB, processes, `Library.Load`.
- `#if wasm` / `#if browser` select code for the page. A page of your own: a toolchain with `"extends": "emscripten"` and `--shell-file` in `args`.
