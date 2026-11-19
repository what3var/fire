# forge on the command line

`forge` (the compiler, `src/fire.Compiler`) runs, builds and tests fire programs. spark uses the same code; everything spark does can be done with
`forge` and `ember` (the [package manager](../packages/README.md)).

## Commands

| Command | What it does |
| --- | --- |
| `forge run <file>... [-- args]` | compiles the files into one program and runs it in the VM; what follows `--` is `Environment.Args` |
| `forge run <project.fireproj \| solution.firesln> [-p name]` | runs a project (of a solution: the startup project, or `-p`) |
| `forge build <file \| project \| solution> [-o out]` | a program: a self-contained VM program ([the VM in C with the program behind it](#programs-of-the-vm-build---engine-vm)), or with `--engine native` a native one; a library project: a package (`.fpk`) |
| `forge native <file>... [-o out.c]` | only translates to C (next to the runtime header), without building |
| `forge bytecode <file>... [-o out.fbc]` | compiles the files into one program and writes it as a program file (`.fbc`, the bytecode with everything a VM needs: [the format](../spec/BYTECODE.md#48-das-dateiformat-fbc)) |
| `forge dump <file>... \| <program.fbc> [-o file]` | a program as text - everything a program file holds, one thing per line, made to be compared (two builds, two compilers) |
| `forge build-vm <folder> [--variant v]... [--all]` | builds the VM in C (the program `fire`, [VM_C.md](../spec/VM_C.md#das-programm-fire-schritt-7)) for this machine into the folder - the build of forge does it next to forge; `--variant i32`, `f32`, `i32-f32`, `i16`, `i16-f32` (or `--all`): the VMs of `#intwidth`/`#floatwidth` |
| `forge tables <folder>` | writes the tables of the compiler - opcodes, error messages, the natives every program has, the prelude - as files (`native/tables` of the repository: what the compiler and the VM in C read) |
| `forge tokens <file>` / `forge ast <file>` | the tokens / the syntax tree of a source as text, with the diagnostics (`--list <files> --out <folder>` for many) - the measure of the frontend in C, [FORGE_C.md](../spec/FORGE_C.md) |
| `forge test [file \| project \| solution] [--filter A.B]` | runs the `[Test]` methods ([testing](testing.md)); exit code 0 when all passed |
| `forge ui <file.fxml> [-o file]` | the script generated from a [markup](../ui/markup.md) file (to look at it; `#include` does it on the fly) |
| `forge biscuit <file.biscuit> [-o file]` | the script generated from a [schematic](../circuits/README.md) |
| `forge fdata <file.fdata> [-o file]` | the code generated from a [data model](../data/designer.md) |
| `forge stats <before.stats.json> <after.stats.json>` | two native builds made with `--stats` side by side: the size, the code by origin, the data, the fire functions by module, the functions that changed most |
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
| `--toolchain <name>` | the C compiler (`gcc`, `clang`, `msvc`, `tcc`, ...); also for `publish`. Without it: the project's, else the machine's Build compiler (`build`, `test`) or Publish compiler (`publish`) - spark's *Tools > Options > Compilers*, `Toolchain/toolchain.json` |
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
| `--stats` | a native build reports what it is made of (gcc, clang, MinGW, Open Watcom): the code by origin from the linker's map (the program, each module, the runtime, the C of each package, the C library; with `--linkage static` the code of each library module - its members, its helpers, its copy of the runtime), the data, the typing of the fire functions, the biggest functions; `<program>.stats.txt`/`.json` next to the program |
| `-p <project>` | the project of a solution |
| `--all` | `build` of a solution: every project in the order of its references - a library is compiled (for the target), a program built into `bin/<target>-<mode>/` (or its `output`); a project whose library failed is skipped (spark's *Build Solution*) |
| `--check` | `build` of a library project: only compiled (for the target of `-t` or the project), not packed |
| `--debug-info` | a native `build` for the debugger: without optimisation, with debug information, not stripped (spark's native *Debug*) |
| `--overlay <source>=<file>` | `build`/`publish`: the text of `<source>` is read from `<file>` - an editor's unsaved text (repeatable; the source need not exist) |
| `--report json` | `build`/`publish`: the [report for tools](#the-report-for-tools) on stdout instead of the text for people |

The command line goes before the project's settings, which go before the directives in the source and `fire.native.json`.

## Programs of the VM (`build --engine vm`)

`forge build x.fire` (the engine `vm`, the default) writes **one executable**: the VM in C (`fire`, `native/vm`, [VM_C.md](../spec/VM_C.md)) with the
program behind it - the program file (`.fbc`, as `forge bytecode` writes it, without line tables) and the libraries of the natives of its packages
(`libfire_pkg_*`, `libfire_gfx` for graphics and windows). Nothing else is needed on the machine that runs it: no .NET, no files next to it.

```bash
forge build hello.fire -o hello && ./hello a b      # Environment.Args: a b - every argument is the program's
```

* **Its libraries**: written by the first start into a folder of the user's cache named by their checksums (`FIRE_VM_CACHE`, else
  `$XDG_CACHE_HOME/fire/native`, `~/.cache/fire/native`, `%LOCALAPPDATA%\fire\native`) and loaded from there; a later start takes them when their
  length and checksum are right (a damaged one is written again). Windows cannot load a library from memory - the same way for every system.
* **Exit code**: 0; 1 for an exception nothing caught (its text on stderr); the code of `terminate(code)`; 2 when the file is damaged.
* **`#intwidth`/`#floatwidth`**: the VM of the program's widths is packed (`fire-i32`, `fire-f32`, `fire-i32-f32`, `fire-i16`, `fire-i16-f32`).
* **Where forge gets `fire`**: `FIRE_VM_EXE` (a file; its variants next to it), else next to forge (the build of forge, spark and the tests compiles it
  there - `src/FireVm.targets`, `forge build-vm` - and spark's publish folder, which `setup.iss` packs, carries it), else forge compiles it now with a
  C compiler of this machine (gcc, clang, tcc, cl - the one that builds the natives of packages) into `<LocalApplicationData>/fire/vm/<rid>/`, once per
  version of its sources. A `fire` next to forge counts when its stamp (`fire.key`) names the sources this forge carries.
* **`#noconsole`**, icon and version (Windows): set in the copy of `fire.exe` before the program goes behind it, as for the .NET runtime.
* **The .NET runtime** (the old way, until M1): `FIRE_VM_ENGINE=dotnet` packs the program with `fire.Runtime` as before (a bundle with the
  payload of `PayloadFile`; it needs an installed .NET).
* A hello world: 748 KB (the VM 642 KB, the program file 107 KB; Linux x64, gcc -O2, stripped) - the .NET one is 729 KB plus the installed .NET.

## The report for tools

`forge build ... --report json` and `forge publish ... --report json` write, instead of the text for people, **one JSON object per line** on stdout
(UTF-8, each line flushed as it is written) - what a tool that starts forge needs to show the build: spark reads it for its build log, build window,
status bar and error list ([spark](spark.md)). Nothing else gets onto stdout: what forge would print there becomes a `log` line, stderr is left for a
crash. Every object has a `type`; a reader ignores types and fields it does not know (the format only grows; `version` in `start` changes only when an
old reader would misread a new report) and shows a line that is no object as a line of the log.

| `type` | Fields | |
| --- | --- | --- |
| `start` | `version` (1), `command` (`build`, `publish`), `text`, `pid` | the build begins (after the standard packages are installed, see `background`) |
| `progress` | `phase`, `text`, `done`, `total` | where the build is: `phase` is `compile-fire`, `package-natives`, `generate-c`, `compile-c`, `link`, `flash` or `pack`; `total` 0 means no count |
| `background` | `text`, `done`, `total` | work without a build window (installing the standard packages, the natives of a package); without `text`: it is over |
| `log` | `text` | a line of the build log: a command, what a compiler said, what was built or copied |
| `diagnostic` | `severity` (`error`, `warning`), `code` (`E4022`, `W204`, [codes](errors.md)), `file` (a path, or null: the file of the build), `line`, `column` (0: unknown), `message`, `project` | an error or warning of the compiler |
| `output` | `kind` (`program`, `folder` - a toolchain that only writes files -, `package`, `publish` - a publish folder), `path`, `project` | what was written |
| `project` | `name`, `ok`, `skipped`, `upToDate`, `error` | one project of `--all` or of a publish |
| `end` | `ok`, `exitCode`, `cancelled`, `summary`, `error` | the last object: the exit code forge ends with, the summary (the program, "Projects: 2 succeeded, ..."), the first error |

```
{"type":"start","version":1,"command":"build","text":"bad.fire","pid":8867}
{"type":"progress","phase":"compile-fire","text":"Compiling fire: parsing 2 sources","done":0,"total":4}
{"type":"diagnostic","severity":"error","code":"E4022","file":null,"line":2,"column":0,"message":"Unknown identifier 'y'"}
{"type":"end","ok":false,"exitCode":1,"cancelled":false,"error":"error E4022: Unknown identifier 'y' (line 2)"}
```

**Cancelling:** the line `cancel` on forge's input (every system), or SIGTERM/SIGINT (Linux, macOS), cancels a build that reports: forge ends the tools it
runs with their process trees, the in-process steps stop at their next checkpoint, what was half written and the temporary folders (`fire-native-*`, ...)
are deleted, and the report ends with `"cancelled": true` and exit code 3. A forge that is killed outright (SIGKILL, a killed process tree on Windows)
cannot clean up: its temporary folder stays in the temp directory. spark therefore asks first and kills only after ten seconds.

**Unsaved text:** `--overlay <source>=<file>` gives forge the text of a source as the editor has it (`<file>` holds it; the source need not exist, so a
never saved script can be built under any name). It applies to the files given, to the sources of a project and its libraries, and to publish.

## Exit codes

`forge run` returns the program's exit code (`terminate(n)`, `Environment.Exit(n)`; 0 at a normal end, 1 for an unhandled exception); a program
that cannot be compiled ends with 1 as well. `build` and `publish` end with 0 (built), 1 (errors in the code, a failed build), 2 (a wrong command line, a
file or configuration that is not there) or 3 (cancelled, with `--report json`). Errors are printed with their code and line (`error E4022: Unknown identifier 'x' (line 3)`, see
[error codes](errors.md)).

## Environment variables

| Variable | Meaning |
| --- | --- |
| `FIRE_DISPLAY` | which window system: `x11`, `wayland`, `win32`, `cocoa`, `web`, `headless` |
| `FIRE_SYSTEM_THEME=off` | do not ask the desktop for dark mode, accent, font and scaling |
| `FIRE_FONT_DIRS` | more folders with fonts (separated by `;`) |
| `FIRE_GFX_GPU=1` | draw every RGBA framebuffer on the GPU (Vulkan) |
| `FIRE_NATIVE_CACHE` | where the cache of native libraries is (`ember cache`) |
| `FIRE_STATS=1` | a native program built with `--stats` (linkage `source`) reports its heap when the main program ends, before its globals go: the objects (their Value slots: numbers, undefined, references; packed bytes), the arrays (Value elements, packed), the buffers, the lambdas, the strings, the C library's heap (glibc) - on the error output |
| `IDF_PATH` | the ESP-IDF folder for the target `esp32` |
