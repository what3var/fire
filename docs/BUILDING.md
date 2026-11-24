# Building fire

fire is C (forge, the compiler: `native/forge`; the VM `fire`: `native/vm`; the runtime and the platforms: `native/`), fire (ember, the package manager:
`tools/ember`) and C# (spark, the editor: `src/fire.Editor`, and its tests: `src/fire.Testing`). The solution `fire.sln` builds all of it, in this order:

| # | Project | What it builds | Into |
| --- | --- | --- | --- |
| 1 | `src/forge-c` | forge-c from `native/forge/forge.c` (one C file) with the C compiler of the machine | `src/forge-c/bin/forge-c[.exe]` |
| 2 | `src/ember` | `forge-c package-dist`: forge, `fire` and its five width variants, `native/`, `Source/` (the standard packages), `Prebuilt/` (their libraries for this machine), libforge/libfirevm; then ember (`forge build tools/ember/ember.fireproj`) | `src/ember/bin/dist/` |
| 3 | spark (`src/fire.Editor`), `src/fire.Testing`, the other C# projects | spark, which copies the folder of step 2 next to itself (`src/ForgeDist.targets`) | `src/fire.Editor/bin/<config>/net10.0/` |

spark depends on `src/ember` (a `ProjectReference` without an assembly), `src/ember` on `src/forge-c`, so the order holds for a build of the solution, of
spark alone and in Visual Studio. Steps 1 and 2 are incremental: forge-c is compiled again only when a source of `native/forge` (or the tables, `native/base`,
`native/abi`) changed, the folder of step 2 only when forge-c, `native/`, `packages/` or `tools/ember` changed - a second build skips both. The first build of
step 2 takes a few minutes (it compiles the six VMs and the libraries of the standard packages).

A publish of spark (`dotnet publish src/fire.Editor -c Release -r win-x64`) puts the same folder into the publish folder - what `setup.iss` packs.

## Building on Windows with Visual Studio

Prerequisites:

- **Visual Studio 2022 or later** with the workloads **".NET desktop development"** (spark, .NET 10) and **"Desktop development with C++"** (MSVC, for forge-c,
  the VM and the libraries of the packages);
- or, instead of the C++ workload, **[w64devkit](https://github.com/skeeto/w64devkit)** (gcc of MinGW) unpacked to `C:\w64devkit` (or `%USERPROFILE%\w64devkit`,
  or with its `bin` on the `PATH`); MSYS2 (`C:\msys64\ucrt64`, `C:\msys64\mingw64`) works too.

Open `fire.sln` and press *Build* (or run `msbuild fire.sln` / `dotnet build fire.sln`). Nothing else is needed: there is no developer prompt to open and no
compiler to name. The compiler of forge-c is, in this order:

1. `FireCC` when it is given (`-p:FireCC=C:\path\to\gcc.exe`; `cl`/`clang-cl` get the options of MSVC, anything else those of gcc);
2. MSVC of a developer prompt (`VCToolsInstallDir` and `INCLUDE` set);
3. the newest Visual Studio with the C++ tools, found with `vswhere` (in the folder of the Visual Studio Installer); the build runs its `vcvars64.bat` before `cl`;
4. `gcc` or `clang` on the `PATH`;
5. w64devkit or MSYS2 where they are usually unpacked.

Without any of them the build of `src/forge-c` **fails** with a message that says what to install - spark is not built without forge. forge itself finds MSVC
the same way when it builds the VM, the libraries of the packages and native programs (`vswhere`, then the environment of `vcvars64.bat` for the compilers it
runs), so the whole folder of step 2 is built with MSVC on a machine that has only Visual Studio.

On Linux and macOS the same solution builds with `dotnet build fire.sln`: forge-c with `cc`, `gcc` or `clang` (the first one on the `PATH`).

## Properties

| Property | Effect |
| --- | --- |
| `BuildForgeDist=false` | Only the C# part: no forge-c, no folder of step 2, nothing copied next to spark. spark then builds forge-c from the sources of the repository when it first needs it (`src/fire.Tooling/Forge.cs`, with gcc or clang). |
| `FireCC=<compiler>` | The C compiler of forge-c (a path or a name on the `PATH`; a name that is not found fails the build). |
| `PrebuildStandard=false` | No `Prebuilt/`: the VM builds the libraries of a package when it first loads it (a quicker first build). |
| `BuildEmber=false` | No ember in the folder. |
| `PublishForge=false` | A publish of spark alone (forge, the VM and ember from a folder made with `native/forge/package-dist.sh`: `iscc /DForgeDist=<folder> setup.iss`). |

Pass them on the command line (`dotnet build fire.sln -p:BuildForgeDist=false`), or, for Visual Studio, as environment variables before it starts
(`set BuildForgeDist=false`) - MSBuild reads them as properties.

## Without the solution

`native/forge/package-dist.sh <folder>` makes the folder of step 2 with `cc` (or `CC=...`); forge-c alone is one command:

```
cc -O2 native/forge/forge.c -o forge-c -lm -lpthread                       (Linux, macOS)
x86_64-w64-mingw32-gcc -O2 native/forge/forge.c -o forge-c.exe -static     (MinGW, w64devkit)
cl /nologo /O2 /std:c11 /utf-8 native\forge\forge.c /Fe:forge-c.exe        (MSVC, in a developer prompt)
```
