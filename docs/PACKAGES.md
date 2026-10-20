# Packages and `ember`

fire can import more than the libraries that come with the compiler (`graphics`, `io`, `time`, ...): a **package** brings one or more imports of its own, for example the
interface to a sensor, a protocol or an operating system API. `#import "name"` finds the imports of the installed packages like the built-in ones.

`ember` is the package manager (the library `fire.Package.Manager`, used by fire.Native and fire.Project, and the command line program `ember` in `src/fire.Ember`). The editor has a small window for it
(*File > Package Manager (ember)...*).

## Using packages

```
ember find sensor          search the package sources (a part of a name or description; no name: everything)
ember install sensorkit    install the newest version (and what it depends on); sensorkit@1.2.0 installs that version
ember install kit.fpk      install a package file
ember list                 the installed packages
ember remove sensorkit     remove a package (not one that another installed package needs)
```

Packages are installed **for the machine**: below `Packages\<name>\` in the folder of the compiler (the folder of `ember` itself). Every script sees the same packages.
A script uses an import of a package like any other:

```
#import "sensorkit"
print(SensorKit.Read(3))
```

An import that is not installed is an error that points to `ember`. The live diagnostics of the editor know the installed imports; after the Package Manager window
installed or removed something, the open scripts are checked again.

### Sources

`ember find` and `ember install` look in the *package sources*. `ember.json` next to the compiler lists them; without the file these are
the folder `PackageSource` next to the compiler and the index on the GitHub page of the packages:

```json
{ "sources": [ "PackageSource", "https://what3var.github.io/fire-packages/index.json", "D:\\my-packages\\index.json" ] }
```

A source is a **folder** (`.fpk` files, and/or an `index.json`) or an **index** (an address of the web, a `file:` address or the path of a json file). The index:

```json
{
  "packages": [
    { "name": "sensorkit", "author": "Jane", "description": "Sensors on the I2C bus",
      "versions": [ { "version": "1.0.0", "url": "sensorkit-1.0.0.fpk", "sha256": "..." } ] }
  ]
}
```

The `url` is relative to the index or absolute; `sha256` (optional) is checked after the download. `ember index Folder [BaseUrl]` writes the `index.json` for the `.fpk`
files of a folder, ready to be put on a web page together with them. A source that cannot be read (no network) is reported and skipped.

## The package file (.fpk)

A `.fpk` is a zip file with a `package.json` in its root and the files that it names (paths relative to the root of the zip):

```json
{
  "format": 1,
  "name": "sensorkit",
  "version": "1.0.0",
  "author": "Jane",
  "description": "Sensors on the I2C bus",
  "license": "MIT",
  "homepage": "https://example.com/sensorkit",
  "dependencies": [ "otherpackage" ],
  "imports": [
    {
      "name": "sensorkit",
      "prelude": "sensorkit/sensorkit.fire",
      "requires": [ "io" ],
      "native": {
        "sources": [ "sensorkit/sensorkit.hpp" ],
        "platforms": [ "posix", "freertos" ],
        "functions": [ { "name": "__sk_read", "arguments": 1, "cpp": "sk_read", "needsList": false, "returnsReference": false } ]
      }
    }
  ]
}
```

* `imports`: the names for `#import "name"` (letters, digits, `_`; not case sensitive; not one of the compiler: `print graphics windows devices io ui linq reflection time random net`).
  A package can bring several. An import can have a **prelude**, **natives**, or both. A package that is only a prelude is allowed.
* `requires`: other imports (of the compiler or of packages) that this one switches on, like `ui` switches on `graphics`.
* `dependencies`: other *packages* that `ember install` installs too.
* `resources` (of an import): the files its prelude embeds with `new Resource("path")` - `{ "icons/up.png": "sensorkit/icons/up.png" }`, the path as the prelude
  writes it (relative to the prelude's folder) and where the file lies in the package (in a forge file: where it is now). `forge build` of a library fills it
  (docs/RESOURCES.md).

### The prelude

Fire source that is added to the program by `#import` - classes, functions, `namespace`s - like the preludes of the compiler's libraries. It works in the virtual machine and in
native builds.

### Natives (C++)

`native.sources` are C++ files, `native.functions` lists the functions that fire code can call by `name` (typically the prelude calls them, but a script may too).
The C++ is written once and works in both engines: a **native build** (`--engine native`, any target) puts the text of the source files into the generated file after the runtime, and
the **virtual machine** loads a shared library that the compiler builds from the same sources for the machine (a generated wrapper exports the C ABI of `native/abi/fire_pkg_abi.h`; the first run
of a script that imports the package takes a few seconds for that and needs a C++ compiler on the machine, unless the package brings a prebuilt library, `native.libraries`).
Per-system code goes into `native.platformSources` (or behind `FIRE_TARGET_*`/`FIRE_HAL_*`), `platforms` limits where the C++ may be used.
How to write a native, what crosses to the VM, prebuilt libraries and packed programs: **docs/PACKAGE_NATIVES.md**.

## The standard libraries (`packages/`, `Source/`)

The libraries of fire - `graphics`, `windows`, `devices`, `io`, `filesystem`, `dialogs`, `ui`, `linq`, `reflection`, `time`, `random`, `testing`, `net`, `tls`, `http`, `gpio`, `i2c`, `spi`, `wifi`, `audio`,
`tracker`, `scene3d` - are ordinary **library projects** (docs/PROJECTS.md) that you can open, change and test like your own: the folder `packages/` of the repository holds the solution
`Packages.firesln` with one project per import:

```
packages/
  Packages.firesln
  time/
    time.fireproj        "name": "fire-time", "import": "time", "standard": true, the natives (sources, functions, reset, link libraries)
    time.fire            the fire code (the prelude of the package)
    native/              the C++ (fire_bridge_time.hpp): the natives, for the VM (a library) and for native builds
    testing/             the tests of the library (forge test), not part of the package
  ui/
    00_core.fire ...     a library may have several files; `#import "graphics"` in them makes graphics a requirement of the package
  ...
```

The build copies the folder next to the programs as **`Source/`** (`src/StandardSources.targets`, imported by the compiler, the editor and the tests) and builds the libraries of the C++ for this
machine into `Prebuilt/` (`forge prebuild-standard Source Prebuilt`). When **spark** or **forge** start (ember asks forge: `forge install-standard`), every project of `Source/Packages.firesln` that
is not installed yet, or whose folder changed since it was installed, is packed (without its tests) and installed - the ones it imports first. A fingerprint of every project folder in
`Packages/.source-stamps.json` keeps the check quick. A prebuilt library goes along when it was built from the same C++, so that the virtual machine needs no C++ compiler for the libraries
as shipped; after a change of the C++ the library is built on first use like that of any package.

**Changing a standard library**: *File > Open the Standard Libraries (Source)* opens the solution in spark. Change a file, run its tests (`forge test`, or the tests of the project), then
*Install* the project (context menu of the project, or *Project > Install Library*): it is packed and installed at once; an installed package of the same version is replaced after a question.
The next start of spark or forge would install it, too.

What makes these projects special is small: `"standard": true` lets a project have the import name of a library of the compiler (`time`, `graphics`, ...), and `"native": { "discover": false,
"functions": [...] }` names the natives explicitly (their C++ names are in namespaces such as `io::FileOpen`). Five imports have natives that are part of the virtual machine (C#): `graphics`,
`windows`, `ui`, `linq`, `reflection` - their fire code still comes from the package, and for native builds the C++ of `graphics` and `windows` too. Only the core prelude (`Exception`,
`List<T>`, `Math`, string methods, ...) is built into the compiler.

Inside its own project a library is the program: `#import "time"` in the tests of `time` means the project's files (and its C++, built for the VM), not the installed package.

## Making packages

```
ember create Kit.json      a description with example values and the example files (a prelude and a C++ native) next to it
ember blank Kit.json       a description with all fields there but empty
ember forge Kit.json       builds the package: Kit.json\..\build\name-version.fpk   (-o Dir: another folder)
```

The file that `forge` reads (the *forge file*) is the same description as the `package.json`, but its paths are **absolute** (or relative to the file itself): they say where the
prelude and the C++ files are on this machine. `forge` puts every file below a folder of its import in the zip, writes the `package.json` with the relative paths, and keeps a copy of the forge
file - with absolute paths - in `Dir\json\name-version.json`. Open that file later to forge the package again (`ember forge Dir\json\name-version.json`).

## Where it is in the code

* `src/fire.Package.Manager`: the manifest (`PackageManifest`), the package file (`Fpk`), the installed packages (`PackageStore`), the sources (`FolderSource`, `IndexSource`),
  install/remove (`PackageManagerService`). A library: fire.Native and fire.Project reference it (the compiler and the editor get it from them). The command line is `src/fire.Ember` (`Program`), an executable that only uses the library.
* The compiler keeps the imports of packages in its set of imports under the key `pkg:name` (`PackageStore.KeyPrefix`): `ImportedPreludes` resolves `#import`, inserts the prelude
  and registers the names of the natives; `LinkedProgram.PackageNatives` carries those names so that a run in the virtual machine (also of a packed program) keeps the
  indexes of the native calls right.
* The native generator (`CppGenerator.CollectPackages`) checks the platforms, puts the C++ sources of the target into the generated file and calls the functions.
* For the virtual machine `PackageLibrary` (fire.Compiler) builds the shared library of an import (generated wrapper + `native/abi/` + the runtime in library mode, `FIRE_LIBRARY`) or
  finds the prebuilt one; `PackageNativeBinding` (fire.Runtime) loads it and marshals the values. `LinkedProgram.PackageNativeLibraries` names the library of each native; a packed program carries
  the libraries in its payload (`PackagePlan`).
