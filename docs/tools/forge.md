# forge on the command line

`forge` (the compiler, `src/fire.Compiler`) runs, builds and tests fire programs. spark uses the same code; everything spark does can be done with
`forge` and `ember` (the [package manager](../packages/README.md)).

## Commands

| Command | What it does |
| --- | --- |
| `forge run <file>... [-- args]` | compiles the files into one program and runs it in the VM; what follows `--` is `Environment.Args` |
| `forge run <project.fireproj \| solution.firesln> [-p name]` | runs a project (of a solution: the startup project, or `-p`) |
| `forge build <file \| project \| solution> [-o out]` | a program: a self-contained VM program, or with `--engine native` a native one; a library project: a package (`.fpk`) |
| `forge native <file>... [-o out.c]` | only translates to C (next to the runtime header), without building |
| `forge test [file \| project \| solution] [--filter A.B]` | runs the `[Test]` methods ([testing](testing.md)); exit code 0 when all passed |
| `forge ui <file.fxml> [-o file]` | the script generated from a [markup](../ui/markup.md) file (to look at it; `#include` does it on the fly) |
| `forge biscuit <file.biscuit> [-o file]` | the script generated from a [schematic](../circuits/README.md) |
| `forge fdata <file.fdata> [-o file]` | the code generated from a [data model](../data/designer.md) |
| `forge falstad <circuit.txt> [-o file.biscuit]` | converts a circuit of Paul Falstad's simulator into a schematic |
| `forge data status\|migrate\|diff\|import [program] --url <url>` | the migrations of a program on a database and its schema ([Data: migrations](../data/README.md#migrations)): which ran, run them (`--to N`), the missing one as fire code, the tables as classes |

```bash
forge run hello.fire
forge run greet.fire -- Ada
forge build App/App.fireproj --engine native -t linux -m RELEASE
forge test All.firesln --filter MathTests
```

## Options

| Option | Meaning |
| --- | --- |
| `-m DEBUG\|RELEASE\|PERFORMANCE` | execution mode (default: what the source says with `#debug`/`#performance`, else release) |
| `--engine vm\|native` | what `build` makes (default: `fire.native.json`, the project, else `vm`) |
| `-t <target>` | target of a native build: `windows`, `linux`, `macos`, `esp32`, `freertos`, `wasm` or one of `fire.native.json` ([targets](../targets/README.md)) |
| `--toolchain <name>` | the C compiler (`gcc`, `clang`, `msvc`, `tcc`, ...) |
| `--config <file>` | the native configuration (default: the nearest `fire.native.json`) |
| `-f 32\|64`, `-i 32\|64` | width of `float` / `int` (default: `#floatwidth` / `#intwidth` in the source, else 64) |
| `--types lax\|strict` | type conflicts are warnings (lax, default) or errors ([types](../language/02-types.md#the-type-checker-lax-and-strict)) |
| `--stripping 0-3`, `--keep-all` | how much a native build leaves out of what the program does not reach (3, all, is the default; `--keep-all` = 0) |
| `--optimize speed\|size` | the direction of a native build: compiler flags and what the runtime embeds (before the project and `fire.native.json`; default: the target's - size for the Open Watcom targets) |
| `--linkage source\|static\|shared\|plugin`, `--host <program>` | how libraries are linked; a plugin and the program it is for ([libraries](../libraries/README.md)) |
| `--global-jobs safepoints\|sleep\|sync` | when the main program runs `fire global` jobs ([threads](../language/15-threads-and-actors.md)) |
| `-D name` | defines a symbol for `#if` (repeatable) |
| `-o <file>` | the output |
| `--keep` | keeps the generated C files |
| `-p <project>` | the project of a solution |

The command line goes before the project's settings, which go before the directives in the source and `fire.native.json`.

## Exit codes

`forge run` returns the program's exit code (`terminate(n)`, `Environment.Exit(n)`; 0 at a normal end, 1 for an unhandled exception); a program
that cannot be compiled ends with 1 as well. Errors are printed with their code and line (`error E4022: Unknown identifier 'x' (line 3)`, see
[error codes](errors.md)).

## Environment variables

| Variable | Meaning |
| --- | --- |
| `FIRE_DISPLAY` | which window system: `x11`, `wayland`, `win32`, `cocoa`, `web`, `headless` |
| `FIRE_SYSTEM_THEME=off` | do not ask the desktop for dark mode, accent, font and scaling |
| `FIRE_FONT_DIRS` | more folders with fonts (separated by `;`) |
| `FIRE_GFX_GPU=1` | draw every RGBA framebuffer on the GPU (Vulkan) |
| `FIRE_NATIVE_CACHE` | where the cache of native libraries is (`ember cache`) |
| `IDF_PATH` | the ESP-IDF folder for the target `esp32` |
