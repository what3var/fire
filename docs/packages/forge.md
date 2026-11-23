# forge: the compiler in your program

`#import "forge"` puts the compiler of fire into a fire program: fire source or a project becomes a program file (the bytes `forge bytecode` writes), its
warnings and errors come back as data, and the whole command line of forge runs in the program. The package [script](script.md) runs what it translated.
The same in the VM and in a native build, for every target forge builds for.

```
#import "forge"

var c = Forge.Compiler.CompileSource("print(\"hello\")\n", "hello.fire")
if (!c.Succeeded) { print(c.Report()) }                // file(line): error CODE: message - one line per diagnostic
print(c.Program.Length)                                // the program file

var r = Forge.Compiler.Run(["bytecode", "app.fire", "-o", "app.fbc"])
print(r.ExitCode + " " + r.Output)                     // what forge wrote, collected

terminate(Forge.Compiler.Main(Environment.Args))       // forge itself: tools/forge.fire
```

| | |
| --- | --- |
| `Forge.Compiler.CompileSource(source, name = "script.fire", mode = Forge.Mode.Default)` | translates a text as the file `name` (its folder: `#include`, resources) |
| `Forge.Compiler.CompileFile(path, mode)` | translates a `.fire` file or a project (`.fireproj`, `.firesln`: the project to run) |
| `Forge.Compilation` | `Succeeded`, `Program` (byte[]), `PackagePath` (the folders of the libraries of its packages, for the VM), `Diagnostics`, `Errors`, `Report()` |
| `Forge.Diagnostic` | `Severity` (`"error"`/`"warning"`), `Code` (`E4022`, `TC005`), `File`, `Line`, `Column`, `Message`, `Text` (the line as forge prints it) |
| `Forge.Compiler.Main(args)` | the command line of forge (`forge <args>`) in this program: it writes to the program's output; the result is its exit code |
| `Forge.Compiler.Run(args)` | the same, with its output collected: `Forge.CommandResult` - `ExitCode`, `Output`, `Errors` (also what the programs of `forge run` write) |
| `Forge.Compiler.Available()`, `LoadError()` | whether the compiler can be used (it always can with the forge in C; see below) |

`Forge.ForgeException` is thrown when the compiler cannot be used. The compiler finds the prelude, the installed packages and `native/` as forge does:
`FIRE_TABLES`, `FIRE_PACKAGES`, `FIRE_NATIVE_ROOT`, else next to the program (a distribution of `forge package-dist`: `native/tables`, `Packages/`, `native/`).
Translations and command lines run one at a time in a process (the builder's folders are the process's); `forge serve` does not run in a program.

## How it gets into the program

The compiler is C (`native/forge`, [FORGE_C.md](../spec/FORGE_C.md)); its interface is `native/abi/fire_embed.h` ([EMBED.md](../spec/EMBED.md)). The package
carries all of it as **one C file**, `native/libforge.c` - `native/forge/forge.c` with every file it includes, written by `forge amalgamate` - and names it in
`native.units` of its project ([natives](natives.md)): forge compiles it as a translation unit of its own and links it into the program, and into the library
of the package for the VM. So a program that imports forge needs nothing next to it, and forge builds it for any target it has a C compiler for (Windows from
Linux with MinGW, with gcc, clang or TinyCC). Its bridge (`native/fire_bridge_forge.h`) calls the functions of `fire_embed.h` directly when forge defines
`FIRE_PKG_UNITS_FORGE`; a forge that does not know units (the C# forge) leaves the define out, and the bridge loads libforge as a shared library
(`forge build-embed <folder>`) from `FIRE_EMBED_PATH`, `FIRE_PKG_PATH`, the folder of the package library or of the program.

`packages/forge/native/libforge.c` is generated: after a change below `native/forge`, `native/base`, `native/abi` or `native/tables`, run
`forge-c amalgamate packages/forge/native/libforge.c` (`--check` only says whether it is current).

## forge in fire

`tools/forge.fire` is forge with a frontend in fire - one line that hands its command line to `Forge.Compiler.Main`. Built with forge like any program
(`forge build tools/forge.fire --engine native -t <target>`, or `--engine vm`), it is a forge for that target: the same commands and output as forge-c,
byte for byte, and it builds itself again ([forge](../tools/forge.md)).
